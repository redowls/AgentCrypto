using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skender.Stock.Indicators;
using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.Indicators;

/// <summary>
/// Maintains a per-(symbol, interval) rolling window of recent <see cref="Quote"/>s
/// and, on each closed bar, computes the strategy-required indicator snapshot
/// and writes it to the <see cref="IIndicatorCache"/>.
///
/// State lifecycle:
/// 1. Ingestor calls <see cref="Seed"/> once per (Symbol, Interval) after the
///    REST backfill; this hands the service the warmup history (typically 500
///    bars, plenty for EMA200).
/// 2. CandlePersistor calls <see cref="OnClosedBarAsync"/> for every IsClosed
///    candle it commits (post-flush, so all indicators see the canonical row).
///
/// The window is trimmed to <see cref="MarketDataOptions.IndicatorWindowSize"/>
/// (default 400 bars) — enough for EMA200 to stabilise while keeping per-bar
/// computation cost bounded. We hold the window on the heap; the bounded
/// <c>List&lt;Quote&gt;</c> per pair is well under 50 KB so memory is a non-issue.
/// </summary>
public sealed class IndicatorPreCacheService
{
    // Per-(symbolId, interval) rolling window. List is single-writer (the
    // persistor's consumer task) but we use ConcurrentDictionary because Seed()
    // can be called from the ingestor's startup thread concurrently with the
    // persistor's first OnClosedBarAsync.
    private readonly ConcurrentDictionary<(int SymbolId, string Interval), WindowState> _windows = new();

    private readonly IIndicatorCache _cache;
    private readonly MarketDataOptions _options;
    private readonly ILogger<IndicatorPreCacheService> _log;

    public IndicatorPreCacheService(
        IIndicatorCache cache,
        IOptions<MarketDataOptions> options,
        ILogger<IndicatorPreCacheService> log)
    {
        _cache = cache;
        _options = options.Value;
        _log = log;
    }

    public void Seed(int symbolId, string symbol, string interval, IEnumerable<Candle> closedCandles)
    {
        var window = _windows.GetOrAdd((symbolId, interval),
            _ => new WindowState(symbol));

        lock (window.Gate)
        {
            window.Quotes.Clear();
            foreach (var c in closedCandles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime))
            {
                window.Quotes.Add(ToQuote(c));
            }
            TrimToWindow(window);
        }

        _log.LogDebug(
            "Indicator window seeded {Symbol}/{Interval} count={Count}",
            symbol, interval, window.Quotes.Count);
    }

    public async Task OnClosedBarAsync(int symbolId, string symbol, string interval, Candle candle, CancellationToken cancellationToken)
    {
        if (!candle.IsClosed) return;

        var window = _windows.GetOrAdd((symbolId, interval), _ => new WindowState(symbol));

        IndicatorSnapshot snapshot;
        lock (window.Gate)
        {
            // Idempotency: if the same bar arrives twice, replace rather than
            // append. The persistor batches and may flush a duplicate during
            // restart-overlap; we want the snapshot to converge to the canonical
            // row regardless.
            var quote = ToQuote(candle);
            if (window.Quotes.Count > 0 && window.Quotes[^1].Date == quote.Date)
            {
                window.Quotes[^1] = quote;
            }
            else
            {
                window.Quotes.Add(quote);
            }
            TrimToWindow(window);

            // Crypto VWAP convention: reset at 00:00 UTC each day.
            var sessionStart = candle.OpenTime.Date;
            snapshot = IndicatorComputer.Compute(window.Quotes, sessionStart);
        }

        try
        {
            await _cache.SetAsync(symbol, interval, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cache failure must not break the persistor — log and continue.
            // Strategies that consume indicators will see stale data for a tick;
            // the next closed bar overwrites it.
            _log.LogWarning(ex,
                "Failed to write indicator snapshot for {Symbol}/{Interval}", symbol, interval);
        }
    }

    private void TrimToWindow(WindowState w)
    {
        var max = _options.IndicatorWindowSize;
        if (w.Quotes.Count <= max) return;
        var excess = w.Quotes.Count - max;
        w.Quotes.RemoveRange(0, excess);
    }

    private static Quote ToQuote(Candle c) => new()
    {
        Date   = DateTime.SpecifyKind(c.OpenTime, DateTimeKind.Utc),
        Open   = c.Open,
        High   = c.High,
        Low    = c.Low,
        Close  = c.Close,
        Volume = c.Volume,
    };

    private sealed class WindowState
    {
        public WindowState(string symbol) { Symbol = symbol; }
        public string Symbol { get; }
        public List<Quote> Quotes { get; } = new();
        public object Gate { get; } = new();
    }
}
