using TradingBot.Risk.Configuration;

namespace TradingBot.Risk.Manager;

/// Pure functions used by <see cref="RiskManager"/>. Lives outside the
/// gate class to keep the property + scenario tests trivial — no DB / no DI.
public static class RiskMath
{
    /// §8.4 — drawdown ladder. <paramref name="ladder"/> is evaluated in the
    /// order supplied; the FIRST rung whose <c>DrawdownAtOrAbove</c> is ≤ the
    /// current DD wins. Order MUST be descending (closest-to-zero first) — we
    /// trust the configuration; <see cref="RiskOptions.DrawdownLadder"/> ships
    /// that way.
    ///
    /// Examples (default rungs -0.05/1.00, -0.10/0.50, -0.15/0.25):
    ///   currentDD =  0.00 → 1.00  (≥ -0.05)
    ///   currentDD = -0.04 → 1.00
    ///   currentDD = -0.07 → 0.50
    ///   currentDD = -0.12 → 0.25
    ///   currentDD = -0.20 → 0.00  (no rung matched ⇒ halt)
    public static decimal LadderMultiplier(
        decimal currentDrawdownPct,
        IReadOnlyList<DrawdownLadderRung> ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        for (var i = 0; i < ladder.Count; i++)
        {
            if (currentDrawdownPct >= ladder[i].DrawdownAtOrAbove)
                return ladder[i].Multiplier;
        }
        return 0m;
    }

    /// §8.1 — vol-adjust factor. atr50 of null ⇒ factor 1.0 (warm-up).
    /// ratio = atr14 / atr50:
    ///   ratio &gt; HighRatio  → HighFactor    (e.g. 1.4 → 0.7×)
    ///   ratio &lt; LowRatio   → LowFactor     (e.g. 0.7 → 1.2×)
    ///   else                 → DefaultFactor (1.0×)
    public static decimal VolAdjust(decimal? atr14, decimal? atr50Sma, RiskOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (atr14 is not { } a14 || a14 <= 0m) return opts.VolAdjustDefault;
        if (atr50Sma is not { } a50 || a50 <= 0m) return opts.VolAdjustDefault;

        var ratio = a14 / a50;
        if (ratio > opts.VolAdjustHighRatio) return opts.VolAdjustHighFactor;
        if (ratio < opts.VolAdjustLowRatio)  return opts.VolAdjustLowFactor;
        return opts.VolAdjustDefault;
    }

    /// §8.1 — pre-clamp position quantity from risk dollars and stop distance.
    /// The caller still owes lot/notional clamping and single-symbol cap.
    public static decimal RawQuantity(decimal riskUsd, decimal stopDistance)
    {
        if (stopDistance <= 0m)
            throw new ArgumentOutOfRangeException(nameof(stopDistance), stopDistance, "must be > 0");
        if (riskUsd < 0m)
            throw new ArgumentOutOfRangeException(nameof(riskUsd), riskUsd, "must be ≥ 0");
        return riskUsd / stopDistance;
    }

    /// Convenience for tests: full sizing pipeline minus the lot/notional clamp.
    public static decimal RiskDollars(
        decimal equityUsd,
        decimal kFactor,
        decimal volAdjust,
        RiskOptions opts) =>
        equityUsd * opts.RiskPerTradeFraction * kFactor * volAdjust;
}
