using System.ComponentModel.DataAnnotations;

namespace TradingBot.Strategies.Configuration;

/// <summary>
/// Bound from <c>Strategies:SignalEngine</c>. Engine-level knobs — strategy-specific
/// thresholds live in <see cref="BreakoutDonchianOptions"/> / etc.
/// </summary>
public sealed class SignalEngineOptions
{
    public const string SectionName = "Strategies:SignalEngine";

    /// <summary>Master gate. False ⇒ <see cref="TradingBot.Strategies.Engine.SignalEngine"/>
    /// reads the bar-close channel but skips strategy evaluation entirely. Used to disable
    /// signal generation in production while leaving market-data ingestion running.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum number of trailing closed bars on the primary TF before the
    /// engine will compute a MarketContext. Sets the floor on volume SMA20 +
    /// the 3-bar micro-confirm window. Default 25 covers SMA20 with 5 bars of
    /// padding for the micro-confirm.
    /// </summary>
    [Range(4, 1000)]
    public int ContextWindowBars { get; init; } = 25;

    /// <summary>Capacity of the downstream <c>IGeneratedSignalChannel</c>.</summary>
    [Range(16, 100_000)]
    public int GeneratedSignalChannelCapacity { get; init; } = 1024;

    /// <summary>If true, intervals other than the primary TF of every registered
    /// strategy are silently dropped. False ⇒ log a debug line per dropped event,
    /// helpful in dev to verify subscription wiring.</summary>
    public bool SilentlyDropOtherIntervals { get; init; } = true;
}
