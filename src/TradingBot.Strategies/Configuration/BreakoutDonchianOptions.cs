using System.ComponentModel.DataAnnotations;
using TradingBot.Core.Domain.Enums;

namespace TradingBot.Strategies.Configuration;

/// <summary>
/// Bound from <c>Strategies:BreakoutDonchian</c>. Defaults match §3.1 of the
/// design doc; every threshold is configurable so the parameter sweep in
/// §10 (walk-forward) can override values without code changes.
/// </summary>
public sealed class BreakoutDonchianOptions
{
    public const string SectionName = "Strategies:BreakoutDonchian";

    /// <summary>Master gate. False ⇒ <see cref="TradingBot.Strategies.Strategies.BreakoutDonchianStrategy"/>
    /// always returns null (used to disable a strategy in production without redeploying).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Primary timeframe — §3.1 specifies 1h.</summary>
    [Required, MinLength(1)]
    public string PrimaryTimeframe { get; init; } = CandleIntervals.OneHour;

    /// <summary>Donchian look-back. §3.1: 20 (1h).</summary>
    [Range(2, 500)]
    public int DonchianLength { get; init; } = 20;

    /// <summary>Volume confirmation multiplier — entry requires
    /// Volume ≥ <c>VolumeMultiplier × SMA(volume, VolumeSmaLength)</c>.</summary>
    [Range(0.1, 10.0)]
    public decimal VolumeMultiplier { get; init; } = 1.5m;

    /// <summary>SMA window for volume confirmation (§3.1: 20).</summary>
    [Range(2, 500)]
    public int VolumeSmaLength { get; init; } = 20;

    /// <summary>ADX threshold — entry requires ADX(14) > this. §3.1: 20.</summary>
    [Range(0.0, 100.0)]
    public decimal AdxThreshold { get; init; } = 20m;

    /// <summary>SL × ATR multiplier — §4.3: 1.5 for breakout.</summary>
    [Range(0.1, 20.0)]
    public decimal AtrSlMultiplier { get; init; } = 1.5m;

    /// <summary>TP × ATR multiplier — §4.3: 3.0 for breakout (1:2 RR).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrTpMultiplier { get; init; } = 3.0m;

    /// <summary>Chandelier-exit lookback for the trail (§3.1: 22).</summary>
    [Range(2, 500)]
    public int TrailChandelierLookback { get; init; } = 22;

    /// <summary>Chandelier-exit ATR multiplier (§3.1: 3).</summary>
    [Range(0.1, 20.0)]
    public decimal TrailChandelierAtrMultiplier { get; init; } = 3.0m;

    /// <summary>Activate trailing once price has moved this many R favorable.
    /// §3.1: 1.5R.</summary>
    [Range(0.0, 20.0)]
    public decimal TrailActivationRMultiple { get; init; } = 1.5m;

    /// <summary>Hard time-stop in primary-TF bars (§3.1: 24 = 24h on 1h).</summary>
    [Range(1, 10000)]
    public int TimeStopBars { get; init; } = 24;
}
