using System.ComponentModel.DataAnnotations;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.MarketData.Configuration;

/// <summary>
/// Bound from the "MarketData" configuration section. Validated on startup so
/// a missing/empty subscription set fails fast rather than silently no-oping.
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>
    /// (Symbol, Interval) tuples to backfill at boot and stream live. Empty list
    /// disables ingestion entirely — useful for boot-only smoke tests.
    /// </summary>
    [Required]
    public IReadOnlyList<SubscriptionOptions> Subscriptions { get; init; } =
        Array.Empty<SubscriptionOptions>();

    /// <summary>How many bars to REST-backfill per (Symbol, Interval) at startup.</summary>
    [Range(1, 1000)]
    public int BackfillBarCount { get; init; } = 500;

    /// <summary>Bounded channel capacity. Drop policy is "block on full" — we
    /// back-pressure the WS handler rather than discard ticks.</summary>
    [Range(100, 1_000_000)]
    public int ChannelCapacity { get; init; } = 10_000;

    /// <summary>Persistor: max rows per bulk-upsert flush.</summary>
    [Range(1, 10_000)]
    public int FlushMaxRows { get; init; } = 500;

    /// <summary>Persistor: max time a partial batch may sit before forced flush.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan FlushMaxAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the gap-detection job runs.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan GapScanInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Threshold multiplier on the bar interval. If
    ///   (now_floor_to_interval - latest_stored_open_time) > threshold * interval
    /// the gap detector triggers a backfill.
    /// </summary>
    [Range(2.0, 100.0)]
    public double GapThresholdMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Cap on the per-call REST gap-backfill window. Binance enforces a 1500-bar
    /// limit; we stay conservative at 1000 for headroom.
    /// </summary>
    [Range(1, 1500)]
    public int GapBackfillBarCount { get; init; } = 1000;

    /// <summary>
    /// Indicator pre-cache: rolling-window length per (Symbol, Interval). Must
    /// be ≥ longest indicator look-back (EMA200).
    /// </summary>
    [Range(200, 5000)]
    public int IndicatorWindowSize { get; init; } = 400;

    /// <summary>
    /// When true, CandlePersistor acquires a named system mutex on startup and
    /// refuses to run if another instance already holds it. Use in multi-process
    /// deployments to enforce single-writer semantics.
    /// </summary>
    public bool UseSingletonMutex { get; init; }

    /// <summary>System mutex name (used when <see cref="UseSingletonMutex"/>).</summary>
    [MinLength(1)]
    public string SingletonMutexName { get; init; } = @"Global\TradingBot.CandlePersistor";

    /// <summary>Optional StackExchange.Redis connection string. When null/empty, the
    /// in-memory caches are used and live-candle/indicator data is process-local.</summary>
    public string? RedisConnectionString { get; init; }

    /// <summary>Hash key prefix for <c>LiveCandles</c> in Redis.</summary>
    [MinLength(1)]
    public string LiveCandleKeyPrefix { get; init; } = "live:";

    /// <summary>Hash key prefix for indicator snapshots: <c>ind:{symbol}:{tf}</c>.</summary>
    [MinLength(1)]
    public string IndicatorKeyPrefix { get; init; } = "ind:";
}

public sealed class SubscriptionOptions
{
    [Required, MinLength(1)]
    public string Symbol { get; init; } = string.Empty;

    [Required, MinLength(1)]
    public string Interval { get; init; } = string.Empty;

    public AccountType Account { get; init; } = AccountType.Spot;
}
