using TradingBot.Strategies.Abstractions;

namespace TradingBot.Strategies.Brackets;

/// <summary>
/// ATR-based stop &amp; target math from §4.2. Pure, allocation-free and total
/// (every input maps to a defined output) — strategies use this to derive
/// brackets at signal time, and the same call replays offline against the
/// frozen <c>AtrValue</c> stored on a signal row.
///
/// Multipliers default to the §4.3 table:
///   Breakout      ⇒ 1.5 / 3.0 (1:2)
///   MeanReversion ⇒ 1.0 / 1.5 (1:1.5)
///   Trend         ⇒ 2.0 / 5.0 (1:2.5, with partial)
///
/// Volatility adjustment (final paragraph of §4.2): when current ATR vs its
/// 50-bar SMA is &gt;1.3, scale TP down by 20%; when &lt;0.7, scale TP up by 20%.
/// SL multiplier is left untouched per the spec — the adjustment is for greed
/// management, not stop placement.
/// </summary>
public static class BracketCalculator
{
    /// <summary>Min ATR — any input below this is bumped up to avoid division by
    /// zero or non-physical zero-width brackets in degenerate cases (e.g. a
    /// stale / interpolated bar). 1e-8 is far below any tradable instrument's
    /// tick size, so the clamp never fires on real data.</summary>
    private const decimal AtrFloor = 0.00000001m;

    /// <summary>§4.2 final-paragraph thresholds and adjustment factors.</summary>
    public const decimal VolHighRatio       = 1.3m;
    public const decimal VolLowRatio        = 0.7m;
    public const decimal VolHighTpScale     = 0.8m; // -20%
    public const decimal VolLowTpScale      = 1.2m; // +20%

    /// <summary>Default multipliers per §4.3 table.</summary>
    public static (decimal SlMult, decimal TpMult) DefaultMultipliers(StrategyType s) => s switch
    {
        StrategyType.Breakout      => (1.5m, 3.0m),
        StrategyType.MeanReversion => (1.0m, 1.5m),
        StrategyType.Trend         => (2.0m, 5.0m),
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unknown StrategyType"),
    };

    /// <summary>
    /// Compute SL/TP using the strategy's default multipliers and an optional
    /// volatility adjustment derived from <paramref name="atr14"/> /
    /// <paramref name="atr50Sma"/>.
    /// </summary>
    public static BracketResult Compute(
        decimal     entry,
        decimal     atr14,
        BracketSide side,
        StrategyType strategyType,
        decimal?    atr50Sma = null)
    {
        var (slMult, tpMult) = DefaultMultipliers(strategyType);
        return Compute(entry, atr14, side, slMult, tpMult, atr50Sma);
    }

    /// <summary>
    /// Compute SL/TP with caller-supplied multipliers — used by strategy
    /// modules that read their multipliers from <c>IOptions&lt;T&gt;</c> rather
    /// than the §4.3 defaults.
    /// </summary>
    public static BracketResult Compute(
        decimal     entry,
        decimal     atr14,
        BracketSide side,
        decimal     slMultiplier,
        decimal     tpMultiplier,
        decimal?    atr50Sma)
    {
        if (entry <= 0m)
            throw new ArgumentOutOfRangeException(nameof(entry), entry, "Entry must be positive.");
        if (slMultiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(slMultiplier), slMultiplier, "SL multiplier must be positive.");
        if (tpMultiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tpMultiplier), tpMultiplier, "TP multiplier must be positive.");

        var atr = atr14 < AtrFloor ? AtrFloor : atr14;

        var effTpMult = AdjustTpForVolatility(tpMultiplier, atr14, atr50Sma);

        decimal sl, tp;
        if (side == BracketSide.Long)
        {
            sl = entry - slMultiplier * atr;
            tp = entry + effTpMult     * atr;
        }
        else
        {
            sl = entry + slMultiplier * atr;
            tp = entry - effTpMult     * atr;
        }

        var risk = Math.Abs(entry - sl);
        return new BracketResult(sl, tp, risk, slMultiplier, effTpMult);
    }

    /// <summary>
    /// §4.2 final paragraph: scale TP by current ATR vs its 50-bar SMA.
    /// </summary>
    public static decimal AdjustTpForVolatility(decimal tpMultiplier, decimal atr14, decimal? atr50Sma)
    {
        if (atr50Sma is not decimal sma || sma <= 0m) return tpMultiplier;
        var ratio = atr14 / sma;
        if (ratio > VolHighRatio) return tpMultiplier * VolHighTpScale;
        if (ratio < VolLowRatio)  return tpMultiplier * VolLowTpScale;
        return tpMultiplier;
    }
}
