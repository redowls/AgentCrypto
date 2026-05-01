using Skender.Stock.Indicators;
using TradingBot.Core.Indicators;

namespace TradingBot.MarketData.Indicators;

/// <summary>
/// Pure computation: takes a sorted-ascending series of quotes and produces a
/// single <see cref="IndicatorSnapshot"/> at the latest bar. No state, no IO,
/// no clock — that lets us unit test it deterministically with a synthetic
/// quote series.
///
/// The set of indicators is fixed by the strategy spec:
///   ATR(14), EMA(9/21/50/200), ADX(14) + DI lines, RSI(14), BB(20,2) + Width,
///   Donchian(20), VWAP-session, plus regime-classifier rolling context:
///   ATR50_SMA, BBW_SMA50, BBW percentile rank, prior-bar ADX/BBW.
///
/// Skender 2.6 returns most indicators as <c>double?</c>. We narrow back to
/// <c>decimal?</c> at this seam so downstream code (DB, Redis) carries a
/// consistent currency-precision type.
/// </summary>
public static class IndicatorComputer
{
    private const int RollingContextWindow = 50;

    /// <summary>
    /// Compute the latest snapshot. <paramref name="sessionStartUtc"/> anchors
    /// the running VWAP — pass the start of the current trading session
    /// (typically 00:00 UTC for crypto).
    /// </summary>
    public static IndicatorSnapshot Compute(IReadOnlyList<Quote> quotes, DateTime sessionStartUtc)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        if (quotes.Count == 0)
        {
            return EmptySnapshot(DateTime.UtcNow);
        }

        var lastBarTime = quotes[^1].Date;

        // Skender produces a series for every indicator over the full quote set;
        // we keep references to the series for ADX/ATR/BB so we can pick prior
        // values and compute rolling context (ATR50 SMA, BBW percentile) without
        // re-running the indicators.
        var atrSeries = quotes.GetAtr(14).ToList();
        var adxSeries = quotes.GetAdx(14).ToList();
        var bbSeries  = quotes.GetBollingerBands(20, 2).ToList();

        var ema9   = quotes.GetEma(9).LastOrDefault();
        var ema21  = quotes.GetEma(21).LastOrDefault();
        var ema50  = quotes.GetEma(50).LastOrDefault();
        var ema200 = quotes.GetEma(200).LastOrDefault();
        var rsi    = quotes.GetRsi(14).LastOrDefault();
        var donch  = quotes.GetDonchian(20).LastOrDefault();
        var vwap   = quotes.GetVwap(sessionStartUtc).LastOrDefault();

        var atr      = atrSeries.LastOrDefault();
        var adx      = adxSeries.LastOrDefault();
        var adxPrior = adxSeries.Count >= 2 ? adxSeries[^2] : null;
        var bb       = bbSeries.LastOrDefault();
        var bbPrior  = bbSeries.Count >= 2 ? bbSeries[^2] : null;

        var bbWidth      = ComputeBbWidth(bb);
        var bbWidthPrior = ComputeBbWidth(bbPrior);

        var atr50Sma     = RollingMean(atrSeries.Select(x => x?.Atr).ToList(), RollingContextWindow);
        var bbwSeries    = bbSeries.Select(x => ComputeBbWidthRaw(x)).ToList();
        var bbwSma50     = RollingMean(bbwSeries, RollingContextWindow);
        var bbwPctRank   = PercentileRank(bbwSeries, RollingContextWindow);

        return new IndicatorSnapshot(
            AsOfUtc:                lastBarTime,
            Atr14:                  AsDecimal(atr?.Atr),
            Ema9:                   AsDecimal(ema9?.Ema),
            Ema21:                  AsDecimal(ema21?.Ema),
            Ema50:                  AsDecimal(ema50?.Ema),
            Ema200:                 AsDecimal(ema200?.Ema),
            Adx14:                  AsDecimal(adx?.Adx),
            PlusDi14:               AsDecimal(adx?.Pdi),
            MinusDi14:              AsDecimal(adx?.Mdi),
            Rsi14:                  AsDecimal(rsi?.Rsi),
            BbUpper:                AsDecimal(bb?.UpperBand),
            BbMid:                  AsDecimal(bb?.Sma),
            BbLower:                AsDecimal(bb?.LowerBand),
            BbWidth:                bbWidth,
            // Donchian's bands are exposed as decimal? in Skender 2.6 (no
            // overflow risk like double-typed indicators) — pass through.
            DonchianUpper:          donch?.UpperBand,
            DonchianLower:          donch?.LowerBand,
            VwapSession:            AsDecimal(vwap?.Vwap),
            Atr50Sma:               AsDecimal(atr50Sma),
            BbWidthSma50:           bbwSma50 is double v ? AsDecimal(v) : null,
            BbWidthPercentileRank:  bbwPctRank is double r ? AsDecimal(r) : null,
            BbWidthPrev:            bbWidthPrior,
            AdxPrev:                AsDecimal(adxPrior?.Adx));
    }

    private static IndicatorSnapshot EmptySnapshot(DateTime asOf) => new(
        AsOfUtc: asOf,
        Atr14: null, Ema9: null, Ema21: null, Ema50: null, Ema200: null,
        Adx14: null, PlusDi14: null, MinusDi14: null, Rsi14: null,
        BbUpper: null, BbMid: null, BbLower: null, BbWidth: null,
        DonchianUpper: null, DonchianLower: null, VwapSession: null,
        Atr50Sma: null, BbWidthSma50: null, BbWidthPercentileRank: null,
        BbWidthPrev: null, AdxPrev: null);

    private static decimal? ComputeBbWidth(BollingerBandsResult? bb)
    {
        var raw = ComputeBbWidthRaw(bb);
        return raw is double d ? AsDecimal(d) : null;
    }

    // (UpperBand - LowerBand) / Sma — the canonical "BBW" used for compression.
    // Computed manually rather than reading bb.Width: keeps us decoupled from
    // Skender minor-version shifts that have flipped Width's nullable semantics.
    private static double? ComputeBbWidthRaw(BollingerBandsResult? bb)
    {
        if (bb is null) return null;
        if (bb.UpperBand is null || bb.LowerBand is null || bb.Sma is null) return null;
        if (bb.Sma.Value == 0d) return null;
        return (bb.UpperBand.Value - bb.LowerBand.Value) / bb.Sma.Value;
    }

    // Mean of the most recent <paramref name="window"/> non-null values. Returns
    // null when fewer than the full window of values is available — we only
    // expose this rolling context once it's reliable, otherwise the regime
    // classifier sees noise during warm-up.
    private static double? RollingMean(IReadOnlyList<double?> series, int window)
    {
        if (series.Count < window) return null;
        var sum = 0d;
        var count = 0;
        for (var i = series.Count - window; i < series.Count; i++)
        {
            if (series[i] is double v && !double.IsNaN(v) && !double.IsInfinity(v))
            {
                sum += v;
                count++;
            }
        }
        return count == window ? sum / count : (double?)null;
    }

    // Percentile rank of the latest value within the trailing <paramref name="window"/>
    // values: count_of_values_strictly_below_latest / window. Range [0, 1].
    // Returns null when warm-up hasn't filled the window yet.
    private static double? PercentileRank(IReadOnlyList<double?> series, int window)
    {
        if (series.Count < window) return null;
        var latest = series[^1];
        if (latest is not double current || double.IsNaN(current) || double.IsInfinity(current)) return null;

        var below = 0;
        var counted = 0;
        for (var i = series.Count - window; i < series.Count - 1; i++)
        {
            if (series[i] is double v && !double.IsNaN(v) && !double.IsInfinity(v))
            {
                counted++;
                if (v < current) below++;
            }
        }
        if (counted == 0) return null;
        return (double)below / counted;
    }

    private static decimal? AsDecimal(double? v)
    {
        if (v is null) return null;
        if (double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null;
        // decimal can't represent the full double range; clamp to its bounds
        // (indicator values for crypto prices stay well within decimal range,
        // but ATR on a tiny window during warm-up can produce 0 or huge spikes).
        if (v.Value > (double)decimal.MaxValue) return decimal.MaxValue;
        if (v.Value < (double)decimal.MinValue) return decimal.MinValue;
        return (decimal)v.Value;
    }
}
