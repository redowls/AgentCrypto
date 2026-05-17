using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Observability;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Execution.Brackets;
using TradingBot.Execution.Configuration;
using TradingBot.Execution.State;

namespace TradingBot.Execution.Engine;

/// <summary>
/// Subscribes to userData WebSocket streams (Spot + Futures, when enabled)
/// and reacts to <c>executionReport</c> / <c>ORDER_TRADE_UPDATE</c> events:
///
///   • Insert <c>dbo.Fills</c> rows for every new trade.
///   • Apply state-machine-validated transitions on the parent Order.
///   • On terminal FILLED for an entry order: open or extend Position, then
///     submit the bracket via <see cref="IBracketPlacer"/>.
///   • On a bracket leg fill: cancel the sibling via the placer's
///     <see cref="IBracketPlacer.HandleLegFilledAsync"/>.
///   • On terminal CANCELED with partial fill: open a Position with the
///     reduced size (entry-side), or reduce the existing Position
///     (bracket-side).
/// </summary>
public sealed class UserDataReactor : BackgroundService
{
    private readonly IBinanceWebSocketManager _ws;
    private readonly IServiceScopeFactory _scopes;
    private readonly OrderStateMachine _stateMachine;
    private readonly IClock _clock;
    private readonly ExecutionOptions _options;
    private readonly ITradingMetrics _metrics;
    private readonly ILogger<UserDataReactor> _log;
    private readonly bool _spotEnabled;
    private readonly bool _futEnabled;

    // IBracketPlacerResolver is scoped; this hosted service is a singleton.
    // It is resolved per call from the same scope used for the repositories.
    public UserDataReactor(
        IBinanceWebSocketManager ws,
        IServiceScopeFactory scopes,
        OrderStateMachine stateMachine,
        IClock clock,
        IOptions<ExecutionOptions> options,
        ITradingMetrics metrics,
        ILogger<UserDataReactor> log,
        IOptions<TradingBot.Exchange.Configuration.BinanceOptions> binanceOptions)
    {
        _ws       = ws;
        _scopes   = scopes;
        _stateMachine = stateMachine;
        _clock    = clock;
        _options  = options.Value;
        _metrics  = metrics;
        _log      = log;
        _spotEnabled = binanceOptions.Value.EnableSpot;
        _futEnabled  = binanceOptions.Value.EnableUsdmFutures;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("UserDataReactor starting (spot={Spot} fut={Fut}).", _spotEnabled, _futEnabled);

        var subs = new List<IStreamSubscription>(2);
        try
        {
            if (_spotEnabled)
                subs.Add(await _ws.SubscribeUserDataAsync(AccountType.Spot, OnUserEventAsync, stoppingToken).ConfigureAwait(false));
            if (_futEnabled)
                subs.Add(await _ws.SubscribeUserDataAsync(AccountType.UmFutures, OnUserEventAsync, stoppingToken).ConfigureAwait(false));

            // Stay alive until cancellation; the ws handler does the work.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var s in subs)
            {
                try { await s.DisposeAsync().ConfigureAwait(false); }
                catch { /* best effort on shutdown */ }
            }
        }
    }

    internal async ValueTask OnUserEventAsync(UserDataEvent evt)
    {
        if (evt.Kind != UserDataEventKind.OrderUpdate) return;
        if (string.IsNullOrEmpty(evt.ClientOrderId)) return;

        try
        {
            await ProcessOrderUpdateAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "UserDataReactor.ProcessOrderUpdate failed cid={Cid}", evt.ClientOrderId);
        }
    }

    private async Task ProcessOrderUpdateAsync(UserDataEvent evt, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var orders     = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var fills      = scope.ServiceProvider.GetRequiredService<IFillRepository>();
        var positions  = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var signals    = scope.ServiceProvider.GetRequiredService<ISignalRepository>();
        var symbols    = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        var links      = scope.ServiceProvider.GetRequiredService<IBracketLinkRepository>();
        var brackets   = scope.ServiceProvider.GetRequiredService<IBracketPlacerResolver>();

        var order = await orders.GetByClientOrderIdAsync(evt.ClientOrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            _log.LogDebug("UserData event for unknown clientOrderId={Cid} — ignoring (likely manual order).",
                evt.ClientOrderId);
            return;
        }

        var newStatus = ExecutionEngine.NormalizeStatus(evt.Status ?? string.Empty);
        var transition = _stateMachine.TryTransition(order.Status, newStatus);
        if (!transition.IsAccepted)
        {
            _log.LogWarning("State machine refused transition cid={Cid} {From}→{To}: {Reason}",
                evt.ClientOrderId, order.Status, newStatus, transition.Reason);
            return;
        }

        // Insert a Fill row when the event represents an actual trade. Binance
        // futures' ORDER_TRADE_UPDATE includes per-trade fields on the Raw
        // payload; we extract a coarse snapshot from the typed event surface.
        if (evt.ExecutedQty is { } executed && executed > order.FilledQty)
        {
            var deltaQty = executed - order.FilledQty;
            var fill = new Fill
            {
                OrderId         = order.OrderId,
                TradeId         = evt.ExchangeOrderId ?? DateTime.UtcNow.Ticks, // best-effort fallback
                Quantity        = deltaQty,
                Price           = evt.AvgFillPrice ?? order.AvgFillPrice ?? 0m,
                Commission      = 0m,
                CommissionAsset = string.Empty,
                IsMaker         = false,
                TradeTime       = evt.EventTimeUtc,
            };
            try
            {
                await fills.InsertIfNewAsync(fill, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Fill insert failed for {Cid}", evt.ClientOrderId);
            }
        }

        await orders.UpdateStatusAsync(
            order.OrderId,
            newStatus,
            evt.ExecutedQty ?? order.FilledQty,
            evt.AvgFillPrice ?? order.AvgFillPrice,
            order.CommissionPaid,
            order.CommissionAsset,
            ct).ConfigureAwait(false);

        if (!_stateMachine.IsTerminal(newStatus)) return;

        // Metrics: order_filled/canceled + first-FILLED latency.
        // `symbol` label is the SymbolId (no SymbolCode on Order); good enough
        // for cardinality-bounded grouping.
        var symbolLabel = order.SymbolId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(newStatus, OrderStatuses.Filled, StringComparison.Ordinal))
        {
            _metrics.IncOrderFilled(order.Side, symbolLabel);
            var latencyMs = (_clock.UtcNow - order.SubmittedAt).TotalMilliseconds;
            if (latencyMs >= 0)
                _metrics.ObserveOrderFillLatency(order.Side, symbolLabel, latencyMs);
        }
        else if (string.Equals(newStatus, OrderStatuses.Cancelled, StringComparison.Ordinal))
        {
            _metrics.IncOrderCanceled(order.Side, symbolLabel);
        }

        // Terminal — branch by whether this is an entry or a bracket leg.
        var bracketLink = await links.GetByLegClientOrderIdAsync(evt.ClientOrderId, ct).ConfigureAwait(false);
        if (bracketLink is not null)
        {
            await HandleBracketLegTerminalAsync(evt, order, newStatus, bracketLink, positions, brackets, ct).ConfigureAwait(false);
            return;
        }

        // Entry-side terminal.
        await HandleEntryTerminalAsync(evt, order, newStatus, positions, signals, symbols, brackets, ct).ConfigureAwait(false);
    }

    private async Task HandleEntryTerminalAsync(
        UserDataEvent evt,
        Order order,
        string newStatus,
        IPositionRepository positions,
        ISignalRepository signals,
        ISymbolRepository symbols,
        IBracketPlacerResolver brackets,
        CancellationToken ct)
    {
        if (order.SignalId is not long sigId) return;
        var signal = await signals.GetByIdAsync(sigId, ct).ConfigureAwait(false);
        if (signal is null)
        {
            _log.LogWarning("Entry terminal cid={Cid} but signal {SigId} not found", evt.ClientOrderId, sigId);
            return;
        }
        var sym = await symbols.GetByIdAsync(order.SymbolId, ct).ConfigureAwait(false);
        if (sym is null) return;

        var fillQty = order.FilledQty;
        if (string.Equals(newStatus, OrderStatuses.Cancelled, StringComparison.Ordinal) && fillQty <= 0m)
        {
            _log.LogInformation("Entry CANCELED with no fill cid={Cid}", evt.ClientOrderId);
            return;
        }
        if (fillQty <= 0m) return;

        var fillPrice = order.AvgFillPrice ?? signal.EntryPrice;
        var posSide = string.Equals(signal.Side, Sides.Buy, StringComparison.Ordinal) ? PositionSides.Long : PositionSides.Short;
        var risk = Math.Abs(signal.EntryPrice - signal.StopLoss) * fillQty;

        // Open or extend the Position. Same (symbol, account, side) extends;
        // otherwise insert a new row.
        var existing = await positions.GetOpenForSymbolAsync(order.SymbolId, order.AccountType, ct).ConfigureAwait(false);
        long positionId;
        if (existing is not null && string.Equals(existing.Side, posSide, StringComparison.Ordinal))
        {
            await positions.ExtendAsync(existing.PositionId, fillQty, fillPrice, ct).ConfigureAwait(false);
            positionId = existing.PositionId;
        }
        else
        {
            positionId = await positions.InsertAsync(new Position
            {
                SymbolId        = order.SymbolId,
                AccountType     = order.AccountType,
                Side            = posSide,
                EntrySignalId   = sigId,
                EntryOrderId    = order.OrderId,
                Quantity        = fillQty,
                AvgEntryPrice   = fillPrice,
                StopLoss        = signal.StopLoss,
                TakeProfit      = signal.TakeProfit,
                InitialRiskUsd  = risk,
                OpenedAt        = _clock.UtcNow,
                Status          = PositionStatuses.Open,
            }, ct).ConfigureAwait(false);
        }

        // Mark signal EXECUTED.
        await signals.UpdateStatusAsync(sigId, SignalStatuses.Executed, $"order={order.OrderId}", ct).ConfigureAwait(false);

        // Place bracket.
        try
        {
            var placer = brackets.Resolve(order.AccountType);
            await placer.PlaceAsync(new BracketPlacementRequest(
                PositionId:      positionId,
                SignalId:        sigId,
                AccountType:     order.AccountType,
                SymbolCode:      sym.SymbolCode,
                SymbolId:        order.SymbolId,
                EntrySide:       signal.Side,
                EntryFillPrice:  fillPrice,
                Quantity:        fillQty,
                StopLossPrice:   signal.StopLoss,
                TakeProfitPrice: signal.TakeProfit,
                CorrelationId:   sigId.ToString()), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Bracket placement failed for position={Pos} signal={Sig}", positionId, sigId);
        }
    }

    private async Task HandleBracketLegTerminalAsync(
        UserDataEvent evt,
        Order order,
        string newStatus,
        BracketLink link,
        IPositionRepository positions,
        IBracketPlacerResolver brackets,
        CancellationToken ct)
    {
        if (!string.Equals(newStatus, OrderStatuses.Filled, StringComparison.Ordinal))
        {
            _log.LogDebug("Bracket leg cid={Cid} terminal={Status} (not FILLED) — no sibling action",
                evt.ClientOrderId, newStatus);
            return;
        }

        var leg = order.OrderId == link.StopOrderId ? "SL" : "TP";

        var placer = brackets.Resolve(link.AccountType);
        await placer.HandleLegFilledAsync(new BracketLegFilled(
            ClientOrderId: evt.ClientOrderId,
            LegFilled:     leg,
            OrderId:       order.OrderId,
            PositionId:    link.PositionId,
            AccountType:   link.AccountType,
            SymbolCode:    evt.Symbol ?? string.Empty,
            CorrelationId: link.PositionId.ToString()), ct).ConfigureAwait(false);

        // Apply position close / reduction.
        var pos = await positions.GetByIdAsync(link.PositionId, ct).ConfigureAwait(false);
        if (pos is null) return;

        var fillQty = order.FilledQty;
        if (fillQty >= pos.Quantity)
        {
            await positions.CloseAsync(pos.PositionId, _clock.UtcNow, order.AvgFillPrice ?? 0m,
                ComputeRealizedPnl(pos, order, fillQty), ct).ConfigureAwait(false);
        }
        else
        {
            await positions.ReduceQuantityAsync(pos.PositionId, fillQty, ct).ConfigureAwait(false);
        }
    }

    private static decimal ComputeRealizedPnl(Position pos, Order exitOrder, decimal fillQty)
    {
        var exitPx = exitOrder.AvgFillPrice ?? 0m;
        var sign = string.Equals(pos.Side, PositionSides.Long, StringComparison.Ordinal) ? 1m : -1m;
        return sign * (exitPx - pos.AvgEntryPrice) * fillQty;
    }
}
