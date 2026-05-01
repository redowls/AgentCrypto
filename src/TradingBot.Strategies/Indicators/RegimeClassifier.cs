using TradingBot.Core.Indicators;

namespace TradingBot.Strategies.Indicators;

/// <summary>
/// Pure rule-based regime classifier per §3.4 of the design doc:
///
///   TRENDING:    ADX&gt;25 AND BBW expanding
///   RANGING:     ADX&lt;20 AND BBW &lt; 0.7 × SMA(BBW,50)
///   VOLATILE:    ATR &gt; 1.5 × ATR50_SMA AND ADX in [20,25]
///   COMPRESSING: BBW low percentile (≤0.2) AND ADX rising
///
/// All decisions read from the snapshot — the classifier never touches IO,
/// which makes it trivial to unit test with synthetic fixtures and lets it
/// run inside hot paths without async overhead.
///
/// AI confirmation is deferred to S9 (<c>IRegimeConfirmer</c>); this rule-based
/// classifier is the sole regime authority until then.
/// </summary>
public sealed class RegimeClassifier : IRegimeClassifier
{
    // Thresholds named here so the classifier reads as English at the call sites.
    private const decimal AdxTrendThreshold       = 25m;
    private const decimal AdxRangeThreshold       = 20m;
    private const decimal RangingBbwSmaMultiplier = 0.7m;
    private const decimal VolatileAtrMultiplier   = 1.5m;
    private const decimal AdxVolatileLow          = 20m;
    private const decimal AdxVolatileHigh         = 25m;
    private const decimal CompressingPctRankCap   = 0.2m;

    // Floor we apply to a matched-rule confidence so a rule that just barely
    // crosses the threshold doesn't surface as confidence near 0 — that would
    // be misleading downstream (e.g., the §3.4 size-multiplier table). The
    // floor is 0.6 (matched a rule = at least somewhat confident).
    private const decimal MatchedFloor = 0.6m;

    public RegimeClassification Classify(IndicatorSnapshot s)
    {
        // Hard-fail to Unknown when the inputs needed for *any* rule are missing.
        // The pre-cache populates these on a closed bar once warm-up is complete;
        // until then the bot should not trade, so Unknown is the safe answer.
        if (s.Adx14 is null || s.BbWidth is null || s.Atr14 is null)
        {
            return new RegimeClassification(Regime.Unknown, 0m, BuildInputs(s, dirUp: null));
        }

        var adx        = s.Adx14.Value;
        var atr        = s.Atr14.Value;
        var bbw        = s.BbWidth.Value;
        var bbwSma50   = s.BbWidthSma50;
        var atr50      = s.Atr50Sma;
        var bbwPctRank = s.BbWidthPercentileRank;
        var bbwPrev    = s.BbWidthPrev;
        var adxPrev    = s.AdxPrev;

        // Direction: +DI vs -DI is the standard ADX directional read. We fall
        // back to a slope check on EMA21 vs EMA50 if DI lines aren't populated
        // (e.g., during warm-up before ADX has 14+ bars of DI). Either way the
        // direction only resolves TrendingUp vs TrendingDown — it doesn't gate
        // the trending rule itself.
        bool? dirUp = ResolveTrendDirection(s);

        var trendingC    = ScoreTrending(adx, bbw, bbwPrev, bbwSma50);
        var rangingC     = ScoreRanging(adx, bbw, bbwSma50);
        var volatileC    = ScoreVolatile(atr, atr50, adx);
        var compressingC = ScoreCompressing(bbwPctRank, adx, adxPrev);

        // Pick the strongest match. Ties prefer the order in §3.4: trending
        // beats ranging beats volatile beats compressing — codified by giving
        // the earlier rules the right of way at equal scores.
        var best  = Regime.Unknown;
        var bestC = 0m;
        if (trendingC > bestC)
        {
            best  = (dirUp ?? true) ? Regime.TrendingUp : Regime.TrendingDown;
            bestC = trendingC;
        }
        if (rangingC > bestC)     { best = Regime.Ranging;     bestC = rangingC; }
        if (volatileC > bestC)    { best = Regime.Volatile;    bestC = volatileC; }
        if (compressingC > bestC) { best = Regime.Compressing; bestC = compressingC; }

        if (best != Regime.Unknown && bestC < MatchedFloor)
        {
            bestC = MatchedFloor;
        }

        return new RegimeClassification(best, Clamp01(bestC), BuildInputs(s, dirUp));
    }

    public Task<RegimeClassification> ClassifyAsync(IndicatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Classify(snapshot));
    }

    // -------------------------------------------------------------------
    // Per-rule confidence scorers. Each returns 0 when the rule fails its
    // gate; otherwise returns a [0, 1] confidence based on how decisively
    // the inputs satisfy the rule (e.g., ADX=40 is more "trending" than
    // ADX=26).
    // -------------------------------------------------------------------

    private static decimal ScoreTrending(decimal adx, decimal bbw, decimal? bbwPrev, decimal? bbwSma50)
    {
        if (adx <= AdxTrendThreshold) return 0m;

        // "BBW expanding" per §3.4. Two complementary readings:
        //   1. BBW &gt; SMA(BBW, 50) — current volatility is above its trailing
        //      mean. This is the robust definition: it mirrors RANGING's
        //      "BBW &lt; 0.7 × SMA(BBW, 50)" check from the same paragraph.
        //   2. BBW &gt; prior-bar BBW — local expansion in progress. Catches the
        //      early stage of a new trend before the trailing SMA catches up.
        // Either condition counts as "expanding"; both is stronger.
        var aboveSma = bbwSma50 is decimal sma && bbw > sma;
        var aboveBar = bbwPrev is decimal prev && bbw > prev;
        if (!aboveSma && !aboveBar) return 0m;

        // ADX strength: linear from 0 at ADX=25 to 1 at ADX=50+.
        var adxStrength = Clamp01((adx - AdxTrendThreshold) / 25m);

        // Expansion strength: max of the two slope reads we have available.
        var smaSlope = aboveSma && bbwSma50 is decimal s && s > 0m
            ? (bbw - s) / s
            : 0m;
        var barSlope = aboveBar && bbwPrev is decimal p && p > 0m
            ? (bbw - p) / p
            : 0m;
        var slopeStrength = Clamp01(Math.Max(smaSlope, barSlope) * 10m);

        return (adxStrength + slopeStrength) / 2m;
    }

    private static decimal ScoreRanging(decimal adx, decimal bbw, decimal? bbwSma50)
    {
        if (adx >= AdxRangeThreshold) return 0m;
        if (bbwSma50 is not decimal sma) return 0m;
        var threshold = RangingBbwSmaMultiplier * sma;
        if (bbw >= threshold) return 0m;

        var adxLow  = Clamp01((AdxRangeThreshold - adx) / AdxRangeThreshold);
        // How far below the 0.7×SMA threshold are we — capped at the threshold itself.
        var bbwLow  = threshold > 0m ? Clamp01((threshold - bbw) / threshold) : 0m;
        return (adxLow + bbwLow) / 2m;
    }

    private static decimal ScoreVolatile(decimal atr, decimal? atr50, decimal adx)
    {
        if (atr50 is not decimal a50 || a50 <= 0m) return 0m;
        if (atr <= VolatileAtrMultiplier * a50) return 0m;
        if (adx < AdxVolatileLow || adx > AdxVolatileHigh) return 0m;

        // ATR-burst strength: 0 at the 1.5× threshold, 1 at 2.5×+.
        var atrRatio = atr / a50;
        return Clamp01((atrRatio - VolatileAtrMultiplier) / 1m);
    }

    private static decimal ScoreCompressing(decimal? pctRank, decimal adx, decimal? adxPrev)
    {
        if (pctRank is not decimal r || r > CompressingPctRankCap) return 0m;
        if (adxPrev is not decimal prev || adx <= prev) return 0m;

        // Lower percentile rank ⇒ more compressed; full strength at rank 0.
        var rankLow = Clamp01(1m - r / CompressingPctRankCap);
        // ADX rising strength: relative slope, capped at 50%.
        var slopeRel = prev > 0m ? (adx - prev) / prev : 0m;
        var slopeStrength = Clamp01(slopeRel * 2m);
        return (rankLow + slopeStrength) / 2m;
    }

    private static bool? ResolveTrendDirection(IndicatorSnapshot s)
    {
        if (s.PlusDi14 is decimal pdi && s.MinusDi14 is decimal mdi && pdi != mdi)
        {
            return pdi > mdi;
        }
        // Fallback: EMA21 above EMA50 is up-trend, below is down-trend. Used
        // during warm-up when DI lines aren't yet populated, or in the (rare)
        // tie-break case where +DI == -DI.
        if (s.Ema21 is decimal e21 && s.Ema50 is decimal e50 && e21 != e50)
        {
            return e21 > e50;
        }
        return null;
    }

    private static decimal Clamp01(decimal v) =>
        v < 0m ? 0m : v > 1m ? 1m : v;

    // Snapshot of the inputs that drove the classification. Persisted to
    // dbo.Regimes.Inputs so a reviewer can reproduce the call from the row
    // without re-loading candle history.
    private static IReadOnlyDictionary<string, decimal?> BuildInputs(IndicatorSnapshot s, bool? dirUp) =>
        new Dictionary<string, decimal?>(StringComparer.Ordinal)
        {
            ["adx14"]                 = s.Adx14,
            ["adxPrev"]               = s.AdxPrev,
            ["plusDi14"]              = s.PlusDi14,
            ["minusDi14"]             = s.MinusDi14,
            ["atr14"]                 = s.Atr14,
            ["atr50Sma"]              = s.Atr50Sma,
            ["bbWidth"]               = s.BbWidth,
            ["bbWidthPrev"]           = s.BbWidthPrev,
            ["bbWidthSma50"]          = s.BbWidthSma50,
            ["bbWidthPercentileRank"] = s.BbWidthPercentileRank,
            ["ema21"]                 = s.Ema21,
            ["ema50"]                 = s.Ema50,
            ["dirUp"]                 = dirUp is null ? null : (dirUp.Value ? 1m : 0m),
        };
}
