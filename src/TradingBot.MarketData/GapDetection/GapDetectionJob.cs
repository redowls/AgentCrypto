using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.GapDetection;

/// <summary>
/// Quartz job: every <c>GapScanInterval</c> (5 min default), walks each
/// configured (Symbol, Interval, Account) and checks the gap between the
/// expected last-closed-bar OpenTime (= floor(now, interval) - interval) and
/// the latest stored OpenTime in dbo.Candles. If the gap exceeds
/// <c>GapThresholdMultiplier × interval</c>, REST-backfills the missing range
/// onto the kline channel (which the persistor MERGEs idempotently).
///
/// We log a <c>RiskEvents</c> row of severity WARN whenever a gap is detected,
/// so an operator can correlate post-hoc with WS disconnects or rate-limit
/// throttling.
///
/// Concurrency note: <see cref="DisallowConcurrentExecutionAttribute"/> ensures
/// only one instance of this job runs at a time, even if the previous trigger
/// fired and is still mid-pass when the next is due.
/// </summary>
[DisallowConcurrentExecution]
public sealed class GapDetectionJob : IJob
{
    public const string JobKey = "GapDetectionJob";

    private const string Exchange = "BINANCE";
    private const string RiskEventType = "MarketData.Gap";

    private readonly IServiceScopeFactory _scopes;
    private readonly IBinanceGatewayResolver _gateways;
    private readonly IKlineChannel _channel;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<MarketDataOptions> _options;
    private readonly ILogger<GapDetectionJob> _log;

    public GapDetectionJob(
        IServiceScopeFactory scopes,
        IBinanceGatewayResolver gateways,
        IKlineChannel channel,
        IClock clock,
        IOptionsMonitor<MarketDataOptions> options,
        ILogger<GapDetectionJob> log)
    {
        _scopes = scopes;
        _gateways = gateways;
        _channel = channel;
        _clock = clock;
        _options = options;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var opts = _options.CurrentValue;

        if (opts.Subscriptions.Count == 0) return;

        await using var scope = _scopes.CreateAsyncScope();
        var symbolRepo = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        var candleRepo = scope.ServiceProvider.GetRequiredService<ICandleRepository>();
        var riskRepo   = scope.ServiceProvider.GetRequiredService<IRiskEventRepository>();

        foreach (var sub in opts.Subscriptions)
        {
            try
            {
                await ScanOneAsync(sub, opts, symbolRepo, candleRepo, riskRepo, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Gap scan failed for {Symbol}/{Interval}; continuing with next subscription.",
                    sub.Symbol, sub.Interval);
            }
        }
    }

    private async Task ScanOneAsync(
        SubscriptionOptions sub,
        MarketDataOptions opts,
        ISymbolRepository symbolRepo,
        ICandleRepository candleRepo,
        IRiskEventRepository riskRepo,
        CancellationToken ct)
    {
        TimeSpan barInterval;
        try { barInterval = IntervalUtility.ToTimeSpan(sub.Interval); }
        catch (ArgumentOutOfRangeException)
        {
            _log.LogWarning(
                "Skipping gap scan for {Symbol}/{Interval}: interval not supported by gap detector.",
                sub.Symbol, sub.Interval);
            return;
        }

        var symbol = await symbolRepo.GetByExchangeAndCodeAsync(Exchange, sub.Symbol, ct).ConfigureAwait(false);
        if (symbol is null)
        {
            _log.LogWarning("Symbol {Symbol} not found in dbo.Symbols; skipping.", sub.Symbol);
            return;
        }

        var nowUtc = _clock.UtcNow;
        // The most recent bar that *should* be closed: floor(now)/I - I. If we
        // are mid-bar, floor(now) gives us the open-time of the in-progress
        // bar; subtract one interval to get the last fully-closed open-time.
        var expectedLastClosedOpen = IntervalUtility.FloorToInterval(nowUtc, barInterval) - barInterval;

        var latestStored = await candleRepo
            .GetLatestOpenTimeAsync(symbol.SymbolId, sub.Interval, ct)
            .ConfigureAwait(false);

        if (latestStored is null)
        {
            _log.LogInformation(
                "No stored candles for {Symbol}/{Interval}; backfilling {Count} bars.",
                sub.Symbol, sub.Interval, opts.GapBackfillBarCount);
            await BackfillAsync(sub, symbol.SymbolId, opts, startUtc: null, endUtc: null, ct).ConfigureAwait(false);
            return;
        }

        var gap = expectedLastClosedOpen - latestStored.Value;
        var threshold = TimeSpan.FromTicks((long)(barInterval.Ticks * opts.GapThresholdMultiplier));

        if (gap <= threshold) return; // healthy

        _log.LogWarning(
            "Gap detected {Symbol}/{Interval}: latest stored={Stored:o}, expected={Expected:o}, gap={Gap}.",
            sub.Symbol, sub.Interval, latestStored.Value, expectedLastClosedOpen, gap);

        // Open the backfill window from the bar AFTER the latest stored one.
        var fromUtc = latestStored.Value + barInterval;
        // Cap window so a multi-day outage doesn't paginate forever in one pass —
        // the next scheduled run will continue from the new latest.
        var maxWindow = TimeSpan.FromTicks(barInterval.Ticks * opts.GapBackfillBarCount);
        var toUtc = fromUtc + maxWindow > expectedLastClosedOpen + barInterval
            ? expectedLastClosedOpen + barInterval
            : fromUtc + maxWindow;

        await BackfillAsync(sub, symbol.SymbolId, opts, fromUtc, toUtc, ct).ConfigureAwait(false);

        await LogRiskEventAsync(riskRepo, symbol.SymbolId, sub, latestStored.Value, expectedLastClosedOpen, gap, ct)
            .ConfigureAwait(false);
    }

    private async Task BackfillAsync(
        SubscriptionOptions sub,
        int symbolId,
        MarketDataOptions opts,
        DateTime? startUtc,
        DateTime? endUtc,
        CancellationToken ct)
    {
        var gateway = _gateways.Get(sub.Account);
        var bars = await gateway
            .GetKlinesAsync(sub.Symbol, sub.Interval, startUtc, endUtc, opts.GapBackfillBarCount, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Gap backfill {Symbol}/{Interval} fetched {Count} bars (window {From} → {To}).",
            sub.Symbol, sub.Interval, bars.Count, startUtc, endUtc);

        foreach (var bar in bars)
        {
            var evt = new KlineEvent(symbolId, sub.Symbol, sub.Interval, sub.Account, bar, KlineSource.GapBackfill);
            await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    private async Task LogRiskEventAsync(
        IRiskEventRepository repo,
        int symbolId,
        SubscriptionOptions sub,
        DateTime latestStored,
        DateTime expected,
        TimeSpan gap,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            symbol = sub.Symbol,
            interval = sub.Interval,
            account = sub.Account.ToString(),
            latestStoredOpenTimeUtc = latestStored,
            expectedLastClosedOpenTimeUtc = expected,
            gapSeconds = gap.TotalSeconds,
        });

        try
        {
            await repo.InsertAsync(
                new RiskEvent
                {
                    EventTime = _clock.UtcNow,
                    EventType = RiskEventType,
                    Severity = "WARN",
                    SymbolId = symbolId,
                    Payload = payload,
                    Acted = false,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logging the gap is best-effort — we already triggered backfill.
            _log.LogWarning(ex, "Failed to record gap RiskEvent for {Symbol}/{Interval}.", sub.Symbol, sub.Interval);
        }
    }
}
