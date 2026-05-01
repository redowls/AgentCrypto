namespace TradingBot.Core.Indicators;

/// <summary>
/// Market-regime taxonomy from §3.4 of the strategy design doc. The five named
/// regimes drive strategy selection in <c>StrategySelector</c>; <c>Unknown</c>
/// is the explicit "no rule matched" output (no strategy is run while Unknown).
/// </summary>
public enum Regime
{
    Unknown      = 0,
    TrendingUp   = 1,
    TrendingDown = 2,
    Ranging      = 3,
    Volatile     = 4,
    Compressing  = 5,
}

/// <summary>
/// String codes used when persisting a regime to <c>dbo.Signals.Regime</c> /
/// <c>dbo.Regimes.Regime</c>. Values match the design-doc spelling so reports
/// over the SQL table read naturally.
/// </summary>
public static class RegimeCodes
{
    public const string Unknown      = "UNKNOWN";
    public const string TrendingUp   = "TRENDING_UP";
    public const string TrendingDown = "TRENDING_DOWN";
    public const string Ranging      = "RANGING";
    public const string Volatile     = "VOLATILE";
    public const string Compressing  = "COMPRESSING";

    public static string ToCode(Regime r) => r switch
    {
        Regime.TrendingUp   => TrendingUp,
        Regime.TrendingDown => TrendingDown,
        Regime.Ranging      => Ranging,
        Regime.Volatile     => Volatile,
        Regime.Compressing  => Compressing,
        _                   => Unknown,
    };

    public static Regime FromCode(string? code) => code switch
    {
        TrendingUp   => Regime.TrendingUp,
        TrendingDown => Regime.TrendingDown,
        Ranging      => Regime.Ranging,
        Volatile     => Regime.Volatile,
        Compressing  => Regime.Compressing,
        _            => Regime.Unknown,
    };
}
