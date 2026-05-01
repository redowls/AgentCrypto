namespace TradingBot.MarketData.Abstractions;

/// <summary>
/// Converts the textual Binance interval codes ("1m", "5m", "1h", "1d", …)
/// into <see cref="TimeSpan"/> for gap-detection arithmetic and for the
/// accumulator's flush deadline tests. Calendar-based intervals ("1M", "1w",
/// "3d") are intentionally not supported here — the bot trades intra-day, and
/// converting "1 month" to a fixed TimeSpan would lie about boundaries.
/// </summary>
public static class IntervalUtility
{
    public static TimeSpan ToTimeSpan(string interval) => interval switch
    {
        "1m"  => TimeSpan.FromMinutes(1),
        "3m"  => TimeSpan.FromMinutes(3),
        "5m"  => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h"  => TimeSpan.FromHours(1),
        "2h"  => TimeSpan.FromHours(2),
        "4h"  => TimeSpan.FromHours(4),
        "6h"  => TimeSpan.FromHours(6),
        "8h"  => TimeSpan.FromHours(8),
        "12h" => TimeSpan.FromHours(12),
        "1d"  => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(interval), interval,
            "Interval not supported for time arithmetic. Use intra-day intervals only."),
    };

    /// <summary>
    /// Floors a UTC instant to the start of its containing interval bucket.
    /// We use the Unix epoch as the anchor (00:00 1970-01-01 UTC) — this aligns
    /// with Binance's bar boundaries for all intra-day intervals.
    /// </summary>
    public static DateTime FloorToInterval(DateTime utc, TimeSpan interval)
    {
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("Expected UTC DateTime.", nameof(utc));

        var ticks = utc.Ticks - (utc.Ticks % interval.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
