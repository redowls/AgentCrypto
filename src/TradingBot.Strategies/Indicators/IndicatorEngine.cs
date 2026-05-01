using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skender.Stock.Indicators;
using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Configuration;
using TradingBot.MarketData.Indicators;

namespace TradingBot.Strategies.Indicators;

/// <summary>
/// Bar-close-driven indicator snapshot lookup, layered over the S4 pre-cache:
///
///  1. <b>Cache hit (hot path).</b> If the IIndicatorCache holds a snapshot for
///     (symbol, interval) at the requested AsOf (or AsOf falls within the same
///     bar), return it. This is the path strategies hit on every bar close —
///     the IndicatorPreCacheService will have just written the canonical snapshot.
///
///  2. <b>Recompute fallback.</b> Otherwise (cold cache, historical AsOf,
///     mismatch after a Redis flush), pull a sufficient candle window from
///     dbo.Candles and run the same Skender pipeline as the live pre-cache.
///     Same code path → same numbers; we deliberately reuse <see cref="IndicatorComputer"/>
///     rather than re-implement the math here.
/// </summary>
public sealed class IndicatorEngine : IIndicatorEngine
{
    private readonly IIndicatorCache _cache;
    private readonly ICandleRepository _candles;
    private readonly ISymbolRepository _symbols;
    private readonly MarketDataOptions _options;
    private readonly ILogger<IndicatorEngine> _log;

    public IndicatorEngine(
        IIndicatorCache cache,
        ICandleRepository candles,
        ISymbolRepository symbols,
        IOptions<MarketDataOptions> options,
        ILogger<IndicatorEngine> log)
    {
        _cache   = cache;
        _candles = candles;
        _symbols = symbols;
        _options = options.Value;
        _log     = log;
    }

    public Task<IndicatorSnapshot?> GetSnapshotAsync(
        int symbolId, string interval, DateTime asOf, CancellationToken cancellationToken)
        => GetInternalAsync(symbolId, interval, asOf, cancellationToken);

    public Task<IndicatorSnapshot?> GetHtfSnapshotAsync(
        int symbolId, string htfInterval, DateTime asOf, CancellationToken cancellationToken)
        => GetInternalAsync(symbolId, htfInterval, asOf, cancellationToken);

    private async Task<IndicatorSnapshot?> GetInternalAsync(
        int symbolId, string interval, DateTime asOf, CancellationToken cancellationToken)
    {
        var symbol = await _symbols.GetByIdAsync(symbolId, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            _log.LogWarning("IndicatorEngine: symbolId={SymbolId} not found", symbolId);
            return null;
        }
        var symbolCode = symbol.SymbolCode;

        // Hot path: the live pre-cache writes the latest closed-bar snapshot
        // for every (symbol, interval). If asOf matches that bar, we can return
        // immediately without touching SQL.
        var cached = await _cache.TryGetAsync(symbolCode, interval, cancellationToken).ConfigureAwait(false);
        if (cached is not null && SnapshotMatches(cached, asOf, interval))
        {
            return cached;
        }

        // Cold path: load enough candles to cover the longest indicator look-back
        // (EMA200) plus the rolling-context window (50). 400 bars covers both
        // comfortably; we go via the configured window so the engine stays in
        // step with the pre-cache's warm-up budget.
        var windowSize = _options.IndicatorWindowSize;
        var intervalSpan = SafeIntervalToTimeSpan(interval);
        if (intervalSpan is null)
        {
            _log.LogWarning("IndicatorEngine: interval {Interval} not supported for time arithmetic", interval);
            return null;
        }

        // [from, to) — ascending; include the asOf bar itself.
        var to   = asOf + intervalSpan.Value;
        var from = asOf - TimeSpan.FromTicks(intervalSpan.Value.Ticks * windowSize);

        var candles = await _candles.GetRangeAsync(symbolId, interval, from, to, cancellationToken)
            .ConfigureAwait(false);

        // Only closed bars participate — the in-progress bar would skew the
        // last-bar values that the regime classifier reads.
        var quotes = candles.Where(c => c.IsClosed).Select(ToQuote).ToList();
        if (quotes.Count == 0)
        {
            _log.LogDebug(
                "IndicatorEngine: no closed candles for symbol={Symbol} interval={Interval} asOf={AsOf}",
                symbolCode, interval, asOf);
            return null;
        }

        var sessionStart = asOf.Date;
        return IndicatorComputer.Compute(quotes, sessionStart);
    }

    // The cache holds the latest snapshot only. We accept it for the requested
    // asOf when its AsOf is the same bar (matched on the interval-floored
    // OpenTime) or when asOf is in the future of the cache (caller hasn't
    // observed a newer bar yet — the pre-cache will catch up).
    private static bool SnapshotMatches(IndicatorSnapshot snap, DateTime asOf, string interval)
    {
        var span = SafeIntervalToTimeSpan(interval);
        if (span is null) return false;
        // Floor both to the bar boundary and compare.
        var snapBar = FloorToInterval(snap.AsOfUtc, span.Value);
        var askBar  = FloorToInterval(asOf,         span.Value);
        return snapBar == askBar;
    }

    private static TimeSpan? SafeIntervalToTimeSpan(string interval)
    {
        try
        {
            return IntervalUtility.ToTimeSpan(interval);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime FloorToInterval(DateTime utc, TimeSpan interval)
    {
        var ticks = utc.Ticks - (utc.Ticks % interval.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
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
}
