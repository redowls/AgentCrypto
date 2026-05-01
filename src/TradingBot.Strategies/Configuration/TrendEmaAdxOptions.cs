using System.ComponentModel.DataAnnotations;
using TradingBot.Core.Domain.Enums;

namespace TradingBot.Strategies.Configuration;

/// <summary>
/// Bound from <c>Strategies:TrendEmaAdx</c>. Defaults match §3.3.
/// </summary>
public sealed class TrendEmaAdxOptions
{
    public const string SectionName = "Strategies:TrendEmaAdx";

    public bool Enabled { get; init; } = true;

    /// <summary>Primary timeframe — §3.3: 1h.</summary>
    [Required, MinLength(1)]
    public string PrimaryTimeframe { get; init; } = CandleIntervals.OneHour;

    /// <summary>Higher timeframe used for trend alignment (§3.3: 4h, EMA200).</summary>
    [Required, MinLength(1)]
    public string HigherTimeframe { get; init; } = CandleIntervals.FourHours;

    /// <summary>ADX threshold (§3.3: 25).</summary>
    [Range(0.0, 100.0)]
    public decimal AdxThreshold { get; init; } = 25m;

    /// <summary>SL × ATR multiplier (§4.3: 2.0 for trend).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrSlMultiplier { get; init; } = 2.0m;

    /// <summary>TP × ATR multiplier (§4.3: 5.0 for trend; partial 50% at +2R, runner trails).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrTpMultiplier { get; init; } = 5.0m;

    /// <summary>Skip entry when current bar range &gt; this × ATR(14). Approximation
    /// of §3.3's "range &gt; 2.5×ATR(20)" exhaustion-bar guard — we substitute
    /// ATR(14) since that's what the snapshot carries (the difference is &lt; 5%
    /// for any liquid pair).</summary>
    [Range(0.1, 100.0)]
    public decimal ExplosiveBarAtrMultiplier { get; init; } = 2.5m;

    /// <summary>Trail Chandelier ATR multiplier (§3.3 alt: 2×ATR Chandelier).</summary>
    [Range(0.1, 20.0)]
    public decimal TrailChandelierAtrMultiplier { get; init; } = 2.0m;

    /// <summary>Activate trail once price moves this many R favorable
    /// (§3.3: trail at +1R; partial exit at +2R is handled by the position manager).</summary>
    [Range(0.0, 20.0)]
    public decimal TrailActivationRMultiple { get; init; } = 1.0m;

    /// <summary>Trail Chandelier lookback (§3.3 reuses §3.1's 22).</summary>
    [Range(2, 500)]
    public int TrailChandelierLookback { get; init; } = 22;

    /// <summary>Hard time-stop in primary-TF bars (§3.3: 5 days = 120 bars on 1h).</summary>
    [Range(1, 10000)]
    public int TimeStopBars { get; init; } = 120;

    /// <summary>If true, allow short entries on bearish crossover (mirror of long entry).</summary>
    public bool AllowShorts { get; init; } = true;
}
