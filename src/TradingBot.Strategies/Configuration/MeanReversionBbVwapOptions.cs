using System.ComponentModel.DataAnnotations;
using TradingBot.Core.Domain.Enums;

namespace TradingBot.Strategies.Configuration;

/// <summary>
/// Bound from <c>Strategies:MeanReversionBbVwap</c>. Defaults match §3.2.
/// </summary>
public sealed class MeanReversionBbVwapOptions
{
    public const string SectionName = "Strategies:MeanReversionBbVwap";

    public bool Enabled { get; init; } = true;

    /// <summary>Primary timeframe — §3.2: 15m.</summary>
    [Required, MinLength(1)]
    public string PrimaryTimeframe { get; init; } = CandleIntervals.FifteenMinutes;

    /// <summary>RSI(14) oversold threshold — entry requires RSI &lt; this for longs.</summary>
    [Range(0.0, 100.0)]
    public decimal RsiOversold { get; init; } = 25m;

    /// <summary>RSI(14) overbought threshold — entry requires RSI &gt; this for shorts.</summary>
    [Range(0.0, 100.0)]
    public decimal RsiOverbought { get; init; } = 75m;

    /// <summary>VWAP "value zone" buffer. Long entry requires
    /// <c>Close &gt; VWAP × (1 − VwapBufferPct)</c> (§3.2: 0.985 = 1.5% below).</summary>
    [Range(0.0, 0.5)]
    public decimal VwapBufferPct { get; init; } = 0.015m;

    /// <summary>ADX cap — entry requires ADX(14) &lt; this. §3.2: 25.</summary>
    [Range(0.0, 100.0)]
    public decimal AdxMaxThreshold { get; init; } = 25m;

    /// <summary>SL × ATR multiplier (§4.3: 1.0 for mean reversion).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrSlMultiplier { get; init; } = 1.0m;

    /// <summary>TP × ATR multiplier (§4.3: 1.5 for mean reversion).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrTpMultiplier { get; init; } = 1.5m;

    /// <summary>Hard time-stop in primary-TF bars (§3.2: 8 = 2h on 15m).</summary>
    [Range(1, 10000)]
    public int TimeStopBars { get; init; } = 8;

    /// <summary>Require <c>Close &gt; Close[N]</c> as the §3.2 micro-confirm.
    /// True ⇒ require the bar 3 closes ago to be present and the gate to fire;
    /// false ⇒ skip the micro-confirm (used in tests where context lacks history).</summary>
    public bool RequireMicroConfirm { get; init; } = true;

    /// <summary>Bar lookback for the micro-confirm (§3.2: 3).</summary>
    [Range(1, 50)]
    public int MicroConfirmLookbackBars { get; init; } = 3;
}
