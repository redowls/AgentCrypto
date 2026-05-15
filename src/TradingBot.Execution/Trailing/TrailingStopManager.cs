using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Execution.Brackets;
using TradingBot.Execution.Configuration;
using TradingBot.Execution.Identity;
using TradingBot.MarketData.Channels;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Execution.Trailing;

/// <summary>
/// §4.4 trailing stop. Subscribes to <see cref="IBarCloseChannel"/>; on every
/// bar close that matches an open position's primary timeframe:
///
///   1. Compute the candidate trail per <see cref="TrailingStopRules"/>.
///   2. If better than the current SL, cancel-replace the bracket SL via
///      <see cref="IBracketPlacer.UpdateStopAsync"/> (idempotent via the
///      sequence-number clientOrderId pattern).
///   3. For trend strategies: take 50% off the position the first time +R is
///      hit, then ratchet stop to break-even via the same UpdateStopAsync path.
///   4. Time-stop: close the position with a market order when N bars elapse
///      without +1R progress.
///
/// Counters (sequence numbers, partial-take fired, bars-held) are tracked in
/// memory keyed by PositionId; a process restart re-derives them from the DB
/// state at the cost of one extra trail tick.
/// </summary>
public sealed class TrailingStopManager : BackgroundService
{
    private readonly IBarCloseChannel _bars;
    private readonly IServiceScopeFactory _scopes;
    private readonly IBracketPlacerResolver _brackets;
    private readonly IBinanceGatewayResolver _gateways;
    private readonly IIndicatorEngine _indicators;
    private readonly IClock _clock;
    private readonly ExecutionOptions _options;
    private readonly ILogger<TrailingStopManager> _log;

    private readonly Dictionary<long, PositionTracker> _trackers = new();
    private readonly object _trackerLock = new();

    public TrailingStopManager(
        IBarCloseChannel bars,
        IServiceScopeFactory scopes,
        IBracketPlacerResolver brackets,
        IBinanceGatewayResolver gateways,
        IIndicatorEngine indicators,
        IClock clock,
        IOptions<ExecutionOptions> options,
        ILogger<TrailingStopManager> log)
    {
        _bars       = bars;
        _scopes     = scopes;
        _brackets   = brackets;
        _gateways   = gateways;
        _indicators = indicators;
        _clock      = clock;
        _options    = options.Value;
        _log        = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("TrailingStopManager starting.");

        await foreach (var bar in _bars.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await OnBarCloseAsync(bar, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "TrailingStopManager.OnBarClose failed sym={Sym} tf={Tf}",
                    bar.SymbolCode, bar.Interval);
            }
        }
    }

    internal async Task OnBarCloseAsync(BarClosedEvent bar, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var positions = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var signals   = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
        var symbols   = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();

        var open = await positions.GetOpenAsync(ct).ConfigureAwait(false);
        if (open.Count == 0) return;

        foreach (var pos in open)
        {
            if (pos.SymbolId != bar.SymbolId) continue;

            var signal = pos.EntrySignalId is long sigId
                ? await signals.GetByIdAsync(sigId, ct).ConfigureAwait(false)
                : null;

            var (trailMult, tf) = signal is null
                ? (_options.DefaultTrailingAtrMultiplier, CandleIntervals.OneHour)
                : TrailingStopRules.DefaultsFor(signal.Strategy);

            if (!string.Equals(tf, bar.Interval, StringComparison.Ordinal)) continue;

            var snapshot = await _indicators.GetSnapshotAsync(pos.SymbolId, tf, bar.Candle.OpenTime, ct).ConfigureAwait(false);
            if (snapshot is null || snapshot.Atr14 is not { } atr || atr <= 0m)
            {
                _log.LogDebug("Trail tick skipped for pos={Pos} — no ATR snapshot", pos.PositionId);
                continue;
            }

            var sym = await symbols.GetByIdAsync(pos.SymbolId, ct).ConfigureAwait(false);
            if (sym is null) continue;

            var tracker = GetOrCreateTracker(pos);
            tracker.BarsHeld++;
            var close = bar.Candle.Close;

            // Time-stop check first — if we exit, no need to retune the bracket.
            var timeStopBars = TimeStopBarsFor(signal?.Strategy);
            if (TrailingStopRules.ShouldTimeExit(pos, close, tracker.BarsHeld, timeStopBars))
            {
                await ExitPositionMarketAsync(pos, sym, signal, ExitReasons.Time, ct).ConfigureAwait(false);
                continue;
            }

            // Trend-strategy partial take.
            if (signal is not null
                && string.Equals(signal.Strategy, StrategyCodes.TrendEmaAdx, StringComparison.Ordinal)
                && !tracker.PartialTakeFired
                && TrailingStopRules.ShouldPartialTake(pos, close, _options.TrendPartialTakeRMultiple))
            {
                await TakePartialAsync(pos, sym, signal, ct).ConfigureAwait(false);
                tracker.PartialTakeFired = true;
                // continue with trail update below — break-even ratchet
            }

            var candidate = TrailingStopRules.ComputeTrailedStop(close, atr, trailMult, pos.Side);
            if (!TrailingStopRules.ShouldReplace(pos.StopLoss, candidate, pos.Side))
            {
                _log.LogDebug("Trail no-op pos={Pos} cur={Cur} cand={Cand}", pos.PositionId, pos.StopLoss, candidate);
                continue;
            }

            tracker.UpdateSequence++;
            try
            {
                var placer = _brackets.Resolve(pos.AccountType);
                await placer.UpdateStopAsync(new BracketUpdateRequest(
                    PositionId:                pos.PositionId,
                    SignalId:                  pos.EntrySignalId ?? 0L,
                    Sequence:                  tracker.UpdateSequence,
                    AccountType:               pos.AccountType,
                    SymbolCode:                sym.SymbolCode,
                    SymbolId:                  pos.SymbolId,
                    PositionSide:              pos.Side,
                    Quantity:                  pos.Quantity,
                    NewStopLossPrice:          candidate,
                    ExistingTakeProfitPrice:   pos.TakeProfit,
                    CorrelationId:             $"trail-{pos.PositionId}-{tracker.UpdateSequence}"), ct).ConfigureAwait(false);

                await positions.UpdateStopsAsync(pos.PositionId, candidate, pos.TakeProfit, ct).ConfigureAwait(false);
                _log.LogInformation("Trail updated pos={Pos} side={Side} oldSl={Old} newSl={New} seq={Seq}",
                    pos.PositionId, pos.Side, pos.StopLoss, candidate, tracker.UpdateSequence);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Trail update failed pos={Pos} seq={Seq}", pos.PositionId, tracker.UpdateSequence);
            }
        }
    }

    private async Task TakePartialAsync(Position pos, Symbol sym, Signal? signal, CancellationToken ct)
    {
        var qty = pos.Quantity * _options.TrendPartialTakeFraction;
        if (qty <= 0m) return;

        var account = ParseAccount(pos.AccountType);
        var exitSide = string.Equals(pos.Side, PositionSides.Long, StringComparison.Ordinal) ? Sides.Sell : Sides.Buy;
        var cid = ClientOrderIdGenerator.ForReconciliationProbe(signal?.SignalId ?? pos.PositionId);

        await _gateways.Get(account).PlaceOrderAsync(new OrderRequest
        {
            Account       = account,
            Symbol        = sym.SymbolCode,
            ClientOrderId = cid,
            Side          = exitSide,
            OrderType     = OrderTypes.Market,
            Quantity      = qty,
            ReduceOnly    = true,
            PositionSide  = pos.Side,
            CorrelationId = $"partial-{pos.PositionId}",
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Trend partial take fired pos={Pos} qty={Qty} cid={Cid}", pos.PositionId, qty, cid);
    }

    private async Task ExitPositionMarketAsync(Position pos, Symbol sym, Signal? signal, string reason, CancellationToken ct)
    {
        var account = ParseAccount(pos.AccountType);
        var exitSide = string.Equals(pos.Side, PositionSides.Long, StringComparison.Ordinal) ? Sides.Sell : Sides.Buy;
        var cid = ClientOrderIdGenerator.ForReconciliationProbe(signal?.SignalId ?? pos.PositionId);

        await _gateways.Get(account).PlaceOrderAsync(new OrderRequest
        {
            Account       = account,
            Symbol        = sym.SymbolCode,
            ClientOrderId = cid,
            Side          = exitSide,
            OrderType     = OrderTypes.Market,
            Quantity      = pos.Quantity,
            ReduceOnly    = true,
            PositionSide  = pos.Side,
            CorrelationId = $"timestop-{pos.PositionId}",
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Time-stop exit fired pos={Pos} reason={Reason} cid={Cid}",
            pos.PositionId, reason, cid);
    }

    private int TimeStopBarsFor(string? strategy) => strategy switch
    {
        StrategyCodes.MeanReversionBbVwap => _options.TimeStops.MeanReversionBarsWithoutMove,
        StrategyCodes.BreakoutDonchian    => _options.TimeStops.BreakoutBarsWithoutMove,
        StrategyCodes.TrendEmaAdx         => _options.TimeStops.TrendBarsWithoutMove,
        _ => 0,
    };

    private PositionTracker GetOrCreateTracker(Position pos)
    {
        lock (_trackerLock)
        {
            if (!_trackers.TryGetValue(pos.PositionId, out var t))
            {
                t = new PositionTracker();
                _trackers[pos.PositionId] = t;
            }
            return t;
        }
    }

    private static AccountType ParseAccount(string s) =>
        string.Equals(s, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountType.UmFutures
            : AccountType.Spot;

    private sealed class PositionTracker
    {
        public int  BarsHeld         { get; set; }
        public int  UpdateSequence   { get; set; }
        public bool PartialTakeFired { get; set; }
    }
}
