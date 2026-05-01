using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Channels;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Channels;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Selection;

namespace TradingBot.Strategies.Engine;

/// <summary>
/// §6 — Strategy module orchestrator. Subscribes to <see cref="IBarCloseChannel"/>,
/// for each closed bar:
///
///   1. Pulls the indicator snapshot from S5 (<see cref="IIndicatorEngine"/>).
///   2. Classifies the regime via <see cref="IRegimeClassifier"/>.
///   3. Asks the <see cref="IStrategySelector"/> which strategies are eligible.
///   4. Filters to strategies whose PrimaryTimeframe matches the closed bar's
///      interval — a bar-close on 15m must not run a 1h strategy.
///   5. Builds a <see cref="MarketContext"/> (trailing aggregates + prior snapshot
///      + HTF close) once per bar and reuses it across strategies.
///   6. For each remaining strategy, calls <see cref="IStrategy.Evaluate"/>; for
///      every non-null candidate, persists a <see cref="Signal"/> row with
///      <c>Status=GENERATED</c> and forwards a <see cref="GeneratedSignalEvent"/>
///      to <see cref="IGeneratedSignalChannel"/>.
///
/// Concurrency: BackgroundService runs as a singleton; the bar-close channel
/// has SingleReader=true. Strategy <c>Evaluate</c> is required to be a pure
/// function of inputs (no I/O), so per-bar work is sequential and lock-free.
/// We open a DI scope per bar — IIndicatorEngine and the repositories are scoped.
/// </summary>
public sealed class SignalEngine : BackgroundService
{
    private readonly IBarCloseChannel _barClose;
    private readonly IGeneratedSignalChannel _generated;
    private readonly IServiceScopeFactory _scopes;
    private readonly IRegimeClassifier _regimes;
    private readonly IStrategySelector _selector;
    private readonly IClock _clock;
    private readonly SignalEngineOptions _options;
    private readonly ILogger<SignalEngine> _log;

    public SignalEngine(
        IBarCloseChannel barClose,
        IGeneratedSignalChannel generated,
        IServiceScopeFactory scopes,
        IRegimeClassifier regimes,
        IStrategySelector selector,
        IClock clock,
        IOptions<SignalEngineOptions> options,
        ILogger<SignalEngine> log)
    {
        _barClose   = barClose;
        _generated  = generated;
        _scopes     = scopes;
        _regimes    = regimes;
        _selector   = selector;
        _clock      = clock;
        _options    = options.Value;
        _log        = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SignalEngine is disabled by configuration; idling until shutdown.");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        _log.LogInformation("SignalEngine starting (channel cap={Cap}).", _barClose.Capacity);

        var reader = _barClose.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    try
                    {
                        await HandleBarCloseAsync(evt, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A failure on one bar must not poison the engine — log
                        // and move on. Persistor will keep emitting events.
                        _log.LogError(ex,
                            "SignalEngine: error processing bar-close {Symbol}/{Interval}@{Bar}",
                            evt.SymbolCode, evt.Interval, evt.Candle.OpenTime);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private async Task HandleBarCloseAsync(BarClosedEvent evt, CancellationToken ct)
    {
        // One DI scope per bar — the indicator engine and repos are scoped.
        await using var scope = _scopes.CreateAsyncScope();
        var indicators = scope.ServiceProvider.GetRequiredService<IIndicatorEngine>();
        var candles    = scope.ServiceProvider.GetRequiredService<ICandleRepository>();
        var signalRepo = scope.ServiceProvider.GetRequiredService<ISignalRepository>();

        var snap = await indicators
            .GetSnapshotAsync(evt.SymbolId, evt.Interval, evt.Candle.OpenTime, ct)
            .ConfigureAwait(false);

        if (snap is null)
        {
            _log.LogDebug(
                "SignalEngine: no snapshot for {Symbol}/{Interval}@{Bar} (warm-up); skipping",
                evt.SymbolCode, evt.Interval, evt.Candle.OpenTime);
            return;
        }

        var classification = _regimes.Classify(snap);
        var assignments = _selector.GetActive(classification.Regime);
        if (assignments.Count == 0)
        {
            _log.LogDebug(
                "SignalEngine: regime={Regime} (conf={Conf:F2}) — no strategies active for {Symbol}/{Interval}@{Bar}",
                classification.Regime, classification.Confidence, evt.SymbolCode, evt.Interval, evt.Candle.OpenTime);
            return;
        }

        // Restrict to strategies whose primary timeframe matches this bar.
        // A 1h strategy only fires on 1h bar-closes; if the user subscribes to
        // 15m and 1h, both events will arrive but each picks its own subset.
        var matching = assignments
            .Where(a => string.Equals(a.Strategy.PrimaryTimeframe, evt.Interval, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
        {
            if (!_options.SilentlyDropOtherIntervals)
            {
                _log.LogDebug(
                    "SignalEngine: no eligible strategy for {Symbol}/{Interval}@{Bar} (regime={Regime}, candidates={N})",
                    evt.SymbolCode, evt.Interval, evt.Candle.OpenTime, classification.Regime, assignments.Count);
            }
            return;
        }

        var builder = new MarketContextBuilder(candles, indicators);

        foreach (var assignment in matching)
        {
            var ctx = await builder
                .BuildAsync(
                    evt.SymbolId, evt.SymbolCode, evt.Interval, evt.Candle.OpenTime, evt.Candle,
                    assignment.Strategy.HigherTimeframe, _clock.UtcNow,
                    _options.ContextWindowBars, ct)
                .ConfigureAwait(false);

            if (ctx is null)
            {
                _log.LogDebug(
                    "SignalEngine: could not build MarketContext for {Symbol}/{Interval}@{Bar}; skipping {Strategy}",
                    evt.SymbolCode, evt.Interval, evt.Candle.OpenTime, assignment.Strategy.Name);
                continue;
            }

            // HTF snapshot (only if the strategy declares one).
            IndicatorSnapshot? htfSnap = null;
            if (!string.IsNullOrWhiteSpace(assignment.Strategy.HigherTimeframe))
            {
                htfSnap = await indicators
                    .GetHtfSnapshotAsync(evt.SymbolId, assignment.Strategy.HigherTimeframe!, evt.Candle.OpenTime, ct)
                    .ConfigureAwait(false);
            }

            // Defence-in-depth: the selector already filtered, but the strategy's
            // own AllowedRegimes is the source of truth for its eligibility.
            if (Array.IndexOf(assignment.Strategy.AllowedRegimes, classification.Regime) < 0)
            {
                _log.LogDebug(
                    "SignalEngine: {Strategy} declines regime={Regime}; skipping",
                    assignment.Strategy.Name, classification.Regime);
                continue;
            }

            SignalCandidate? candidate;
            try
            {
                candidate = assignment.Strategy.Evaluate(snap, htfSnap, classification.Regime, ctx);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "SignalEngine: {Strategy} threw on {Symbol}/{Interval}@{Bar}; ignoring",
                    assignment.Strategy.Name, evt.SymbolCode, evt.Interval, evt.Candle.OpenTime);
                continue;
            }

            if (candidate is null) continue;

            var signal = ToSignalRow(evt, candidate, classification, _clock.UtcNow);

            try
            {
                signal.SignalId = await signalRepo.InsertAsync(signal, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "SignalEngine: persist failed for {Strategy} {Symbol}/{Interval}@{Bar}",
                    assignment.Strategy.Name, evt.SymbolCode, evt.Interval, evt.Candle.OpenTime);
                continue;
            }

            _log.LogInformation(
                "SignalEngine: GENERATED {Strategy} {Side} {Symbol}/{Interval}@{Bar} entry={Entry} SL={SL} TP={TP} ATR={ATR} regime={Regime} sizeMult={Size:F2} reason={Reason}",
                signal.Strategy, signal.Side, signal.SymbolId, signal.Interval, signal.BarOpenTime,
                signal.EntryPrice, signal.StopLoss, signal.TakeProfit, signal.AtrValue,
                signal.Regime, assignment.SizeMultiplier, signal.Reason);

            try
            {
                await _generated.Writer
                    .WriteAsync(new GeneratedSignalEvent(signal, assignment.SizeMultiplier), ct)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Downstream consumer is gone; don't tear the engine down — the
                // signal row is durable and a recovery process can pick it up.
                _log.LogWarning(
                    "SignalEngine: GeneratedSignalChannel closed; signal {SignalId} is persisted but not forwarded",
                    signal.SignalId);
            }
        }
    }

    private static Signal ToSignalRow(
        BarClosedEvent evt, SignalCandidate candidate, RegimeClassification regime, DateTime nowUtc) => new()
        {
            SymbolId       = evt.SymbolId,
            Strategy       = candidate.StrategyCode,
            Interval       = evt.Interval,
            BarOpenTime    = evt.Candle.OpenTime,
            Side           = candidate.Side,
            EntryPrice     = candidate.EntryPrice,
            StopLoss       = candidate.StopLoss,
            TakeProfit     = candidate.TakeProfit,
            AtrValue       = candidate.AtrValue,
            Regime         = RegimeCodes.ToCode(regime.Regime),
            SentimentScore = null,
            AiConfidence   = null,
            Confidence     = candidate.Confidence,
            Status         = SignalStatuses.Generated,
            Reason         = candidate.Reason,
            CreatedAt      = nowUtc,
        };
}
