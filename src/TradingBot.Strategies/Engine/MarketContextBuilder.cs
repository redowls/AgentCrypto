using TradingBot.Core.Domain;
using TradingBot.Core.Indicators;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.Strategies.Abstractions;

namespace TradingBot.Strategies.Engine;

/// <summary>
/// Hydrates a <see cref="MarketContext"/> for the SignalEngine: pulls the trailing
/// candle window for VolumeSma20 + Close[3], reads the prior-bar indicator
/// snapshot, and the latest HTF close. All I/O lives here so the strategies
/// stay pure functions of their inputs.
///
/// Extracted from the SignalEngine for testability — the engine itself is a
/// scheduling/routing concern; the per-bar data plumbing is here.
/// </summary>
public sealed class MarketContextBuilder
{
    private readonly ICandleRepository _candles;
    private readonly IIndicatorEngine _indicators;

    public MarketContextBuilder(ICandleRepository candles, IIndicatorEngine indicators)
    {
        _candles    = candles;
        _indicators = indicators;
    }

    /// <summary>
    /// Build the MarketContext for the just-closed bar at <paramref name="barOpenTime"/>
    /// on <paramref name="primaryInterval"/>. Returns null only if the canonical
    /// bar can't be loaded (a pathological race — the persistor publishes the
    /// event after the MERGE).
    /// </summary>
    public async Task<MarketContext?> BuildAsync(
        int       symbolId,
        string    symbolCode,
        string    primaryInterval,
        DateTime  barOpenTime,
        Candle    barCandle,
        string?   higherTimeframe,
        DateTime  nowUtc,
        int       contextWindowBars,
        CancellationToken ct)
    {
        var primarySpan = SafeIntervalToTimeSpan(primaryInterval);
        if (primarySpan is null) return null;

        // Pull the trailing window of bars *including* the just-closed bar.
        // GetRangeAsync is [from, to) and we want [from, barOpenTime + interval).
        var to   = barOpenTime + primarySpan.Value;
        var from = barOpenTime - TimeSpan.FromTicks(primarySpan.Value.Ticks * (contextWindowBars - 1));

        var window = await _candles.GetRangeAsync(symbolId, primaryInterval, from, to, ct).ConfigureAwait(false);

        // Filter to closed bars only — in-progress bars aren't in dbo.Candles
        // anyway, but be defensive.
        var closed = window.Where(c => c.IsClosed)
            .OrderBy(c => c.OpenTime)
            .ToList();

        var (volumeSma20, close3BarsAgo) = ComputeAggregates(closed, barOpenTime);

        // Prior-bar snapshot on the primary TF — used by §3.3 for the EMA cross.
        // null when the bar before barOpenTime isn't available yet (cold start).
        var priorBarOpen = barOpenTime - primarySpan.Value;
        IndicatorSnapshot? priorSnap = null;
        if (closed.Any(c => c.OpenTime == priorBarOpen))
        {
            priorSnap = await _indicators
                .GetSnapshotAsync(symbolId, primaryInterval, priorBarOpen, ct)
                .ConfigureAwait(false);
        }

        // HTF close — only when the strategy declares an HTF. We pull the most
        // recent HTF candle whose close time is ≤ the primary bar open time.
        decimal? htfClose = null;
        if (!string.IsNullOrWhiteSpace(higherTimeframe))
        {
            var htfSpan = SafeIntervalToTimeSpan(higherTimeframe!);
            if (htfSpan is not null)
            {
                // The HTF bar containing the primary close is the one whose open
                // is at or just before barOpenTime. We pull the bar that has just
                // closed at or before barOpenTime + primarySpan to align with §3.3
                // (which checks Close_4h, the most recent 4h close).
                var htfFrom = barOpenTime - TimeSpan.FromTicks(htfSpan.Value.Ticks * 2);
                var htfTo   = barOpenTime + primarySpan.Value;
                var htfBars = await _candles
                    .GetRangeAsync(symbolId, higherTimeframe!, htfFrom, htfTo, ct)
                    .ConfigureAwait(false);
                var lastHtf = htfBars
                    .Where(c => c.IsClosed && c.CloseTime <= barOpenTime + primarySpan.Value)
                    .OrderByDescending(c => c.OpenTime)
                    .FirstOrDefault();
                htfClose = lastHtf?.Close;
            }
        }

        return new MarketContext(
            SymbolId:        symbolId,
            SymbolCode:      symbolCode,
            PrimaryInterval: primaryInterval,
            BarOpenTime:     barOpenTime,
            BarOpen:         barCandle.Open,
            BarHigh:         barCandle.High,
            BarLow:          barCandle.Low,
            BarClose:        barCandle.Close,
            BarVolume:       barCandle.Volume,
            VolumeSma20:     volumeSma20,
            Close3BarsAgo:   close3BarsAgo,
            PriorSnapshot:   priorSnap,
            HtfBarClose:     htfClose,
            NowUtc:          nowUtc);
    }

    /// <summary>
    /// Compute (VolumeSma20, Close[3 bars ago]) from the trailing window. Null
    /// when there isn't enough closed history for the respective aggregate.
    /// </summary>
    private static (decimal? volumeSma20, decimal? close3BarsAgo) ComputeAggregates(
        IReadOnlyList<Candle> ascending, DateTime barOpenTime)
    {
        // The window may include in-progress data above the requested bar; cut
        // at the just-closed bar to compute "as-of" aggregates.
        var trimmed = ascending.Where(c => c.OpenTime <= barOpenTime).ToList();

        decimal? volSma20 = null;
        if (trimmed.Count >= 20)
        {
            decimal sum = 0m;
            for (int i = trimmed.Count - 20; i < trimmed.Count; i++)
            {
                sum += trimmed[i].Volume;
            }
            volSma20 = sum / 20m;
        }

        decimal? close3 = null;
        if (trimmed.Count >= 4)
        {
            // "Close[3]" in §3.2 = close of the bar 3 closes back. With the
            // current bar at index n-1, close[3] = trimmed[n-1-3].Close.
            close3 = trimmed[trimmed.Count - 1 - 3].Close;
        }

        return (volSma20, close3);
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
}
