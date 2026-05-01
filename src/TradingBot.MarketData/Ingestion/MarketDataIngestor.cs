using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;
using TradingBot.MarketData.Indicators;

namespace TradingBot.MarketData.Ingestion;

/// <summary>
/// Owns the producer side of the ingestion pipeline. On startup it walks the
/// configured (Symbol, Interval, Account) subscriptions and, for each:
///   1. Resolves the SymbolId once (cached for the WS callback path).
///   2. REST-backfills the last <c>BackfillBarCount</c> closed bars and pushes
///      them onto the channel, so first-tick consumers see warm history.
///   3. Seeds the indicator pre-cache with the same backfill series.
///   4. Subscribes to the live WS kline stream; every kline event is wrapped
///      in a <see cref="KlineEvent"/> and pushed onto the channel.
///
/// The hosted service intentionally does NOT call <c>WriteAsync</c> from a
/// fire-and-forget task — the WS handler awaits the write so that, when the
/// channel is full, back-pressure propagates to the WebSocket and the socket
/// itself slows down (per spec §1.3 — drop policy = block).
/// </summary>
public sealed class MarketDataIngestor : BackgroundService
{
    private const string Exchange = "BINANCE";

    private readonly IBinanceGatewayResolver _gateways;
    private readonly IBinanceWebSocketManager _ws;
    private readonly IServiceScopeFactory _scopes;
    private readonly IKlineChannel _channel;
    private readonly SymbolMapCache _symbolMap;
    private readonly IndicatorPreCacheService _indicatorPreCache;
    private readonly IOptionsMonitor<MarketDataOptions> _options;
    private readonly ILogger<MarketDataIngestor> _log;

    private readonly List<IStreamSubscription> _subscriptions = new();

    public MarketDataIngestor(
        IBinanceGatewayResolver gateways,
        IBinanceWebSocketManager ws,
        IServiceScopeFactory scopes,
        IKlineChannel channel,
        SymbolMapCache symbolMap,
        IndicatorPreCacheService indicatorPreCache,
        IOptionsMonitor<MarketDataOptions> options,
        ILogger<MarketDataIngestor> log)
    {
        _gateways = gateways;
        _ws = ws;
        _scopes = scopes;
        _channel = channel;
        _symbolMap = symbolMap;
        _indicatorPreCache = indicatorPreCache;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;

        if (opts.Subscriptions.Count == 0)
        {
            _log.LogInformation(
                "MarketDataIngestor has no subscriptions configured; idling.");
            // Block until cancellation so the host doesn't recycle this service.
            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return;
        }

        _log.LogInformation(
            "MarketDataIngestor starting: {Count} subscription(s); backfill={Backfill} bars.",
            opts.Subscriptions.Count, opts.BackfillBarCount);

        foreach (var sub in opts.Subscriptions)
        {
            try
            {
                await StartSubscriptionAsync(sub, opts, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-subscription failure is not fatal — log and continue. Gap
                // detection will catch up the missing range when health returns.
                _log.LogError(ex,
                    "Subscription start failed for {Symbol}/{Interval} ({Account}); other streams continue.",
                    sub.Symbol, sub.Interval, sub.Account);
            }
        }

        // Hold ExecuteAsync until shutdown so DI keeps the subscriptions alive.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task StartSubscriptionAsync(
        SubscriptionOptions sub,
        MarketDataOptions opts,
        CancellationToken ct)
    {
        // 1. Resolve SymbolId once — Symbols catalog is static for the lifetime
        // of an active ticker and we don't want a DB hit per kline.
        int symbolId;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var symbolRepo = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
            symbolId = await _symbolMap.ResolveAsync(symbolRepo, Exchange, sub.Symbol, ct).ConfigureAwait(false);
        }

        // 2. REST backfill. We push every backfill bar onto the channel; the
        // persistor will MERGE — duplicates from a previous run are no-ops.
        var gateway = _gateways.Get(sub.Account);
        var bars = await gateway
            .GetKlinesAsync(sub.Symbol, sub.Interval, startUtc: null, endUtc: null,
                limit: opts.BackfillBarCount, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Backfilled {Count} bars for {Symbol}/{Interval} ({Account}).",
            bars.Count, sub.Symbol, sub.Interval, sub.Account);

        var seedCandles = new List<Candle>(bars.Count);
        foreach (var bar in bars)
        {
            var evt = new KlineEvent(symbolId, sub.Symbol, sub.Interval, sub.Account, bar, KlineSource.RestBackfill);
            await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            seedCandles.Add(CandleMapper.ToCandle(evt));
        }

        // 3. Seed indicator window before WS goes live so the first WS-sourced
        // closed bar produces a fully-warm indicator snapshot.
        _indicatorPreCache.Seed(symbolId, sub.Symbol, sub.Interval, seedCandles);

        // 4. Live WS subscription. The handler awaits WriteAsync — when the
        // channel is full, back-pressure flows back to Binance.Net.
        var subscription = await _ws.SubscribeKlineAsync(
            sub.Account,
            sub.Symbol,
            sub.Interval,
            async kline =>
            {
                var evt = new KlineEvent(symbolId, sub.Symbol, sub.Interval, sub.Account, kline, KlineSource.WebSocket);
                try
                {
                    await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException) { /* shutting down */ }
                catch (OperationCanceledException) { /* shutting down */ }
            },
            ct).ConfigureAwait(false);

        _subscriptions.Add(subscription);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("MarketDataIngestor stopping; tearing down {Count} subscriptions.", _subscriptions.Count);

        foreach (var sub in _subscriptions)
        {
            try { await sub.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing subscription {StreamId}.", sub.StreamId); }
        }

        _subscriptions.Clear();

        // Complete the channel writer so the persistor drains and exits cleanly.
        _channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
