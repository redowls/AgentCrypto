using TradingBot.Core.Domain.Enums;

namespace TradingBot.Strategies.Brackets;

/// <summary>
/// Output of <see cref="BracketCalculator.Compute"/>: SL, TP and the
/// per-unit risk used to size the position downstream (§8 risk manager).
/// </summary>
/// <param name="StopLoss">Initial stop-loss in price terms.</param>
/// <param name="TakeProfit">Initial take-profit (TP1 for staged-exit strategies).</param>
/// <param name="RiskPerUnit">|Entry − StopLoss| — never negative.</param>
/// <param name="EffectiveSlMultiplier">Final SL × ATR multiplier *after* any
/// volatility adjustment (always equal to the input slMult — adjustment is on
/// TP only, per §4.2).</param>
/// <param name="EffectiveTpMultiplier">Final TP × ATR multiplier *after*
/// volatility adjustment.</param>
public sealed record BracketResult(
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskPerUnit,
    decimal EffectiveSlMultiplier,
    decimal EffectiveTpMultiplier)
{
    /// <summary>
    /// Risk-reward ratio: |TP − Entry| / RiskPerUnit. Convenience for logging
    /// and tests.
    /// </summary>
    public decimal RiskReward(decimal entry) =>
        RiskPerUnit > 0m
            ? Math.Abs(TakeProfit - entry) / RiskPerUnit
            : 0m;
}

/// <summary>
/// Strategy direction passed to <see cref="BracketCalculator.Compute"/>. We
/// keep the enum local to the brackets layer rather than reuse <see cref="Sides"/>
/// (a string family) so the calculator's signature stays type-checked.
/// </summary>
public enum BracketSide
{
    Long  = 1,
    Short = 2,
}

public static class BracketSideExtensions
{
    public static string ToSideCode(this BracketSide side) => side switch
    {
        BracketSide.Long  => Sides.Buy,
        BracketSide.Short => Sides.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown BracketSide"),
    };
}
