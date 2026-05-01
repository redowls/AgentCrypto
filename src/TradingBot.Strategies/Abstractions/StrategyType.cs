namespace TradingBot.Strategies.Abstractions;

/// <summary>
/// Discriminator for strategy bracket math (§4.2). The string codes are also
/// used as the <c>dbo.Signals.Strategy</c> value, so reports over the SQL
/// table read identically to the design doc tags.
/// </summary>
public enum StrategyType
{
    /// <summary>§3.1 — Donchian breakout with volume confirmation.</summary>
    Breakout = 1,

    /// <summary>§3.2 — RSI / Bollinger / VWAP mean reversion.</summary>
    MeanReversion = 2,

    /// <summary>§3.3 — EMA9/21 cross with ADX + HTF filter.</summary>
    Trend = 3,
}

/// <summary>
/// Stable string codes persisted to <c>dbo.Signals.Strategy</c>. Values match
/// §3.4 of the design doc verbatim.
/// </summary>
public static class StrategyCodes
{
    public const string BreakoutDonchian      = "BREAKOUT_DON";
    public const string MeanReversionBbVwap   = "MR_BB_VWAP";
    public const string TrendEmaAdx           = "TREND_EMA_ADX";

    public static string ForType(StrategyType t) => t switch
    {
        StrategyType.Breakout      => BreakoutDonchian,
        StrategyType.MeanReversion => MeanReversionBbVwap,
        StrategyType.Trend         => TrendEmaAdx,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown StrategyType"),
    };
}
