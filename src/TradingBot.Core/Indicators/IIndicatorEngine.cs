namespace TradingBot.Core.Indicators;

/// <summary>
/// On-demand indicator snapshot lookup, used by the strategy modules at bar close.
///
/// Hot path: <see cref="GetSnapshotAsync"/> reads the latest pre-cached snapshot
/// produced by <c>IndicatorPreCacheService</c> (S4). When the caller asks for a
/// historical bar (<paramref name="asOf"/> earlier than the cached AsOf) or the
/// cache is cold, the engine recomputes from the candle store using the same
/// Skender pipeline as the live pre-cache.
/// </summary>
public interface IIndicatorEngine
{
    /// <summary>
    /// Returns the indicator snapshot at the bar whose OpenTime matches
    /// <paramref name="asOf"/>. Returns <c>null</c> only when there is
    /// insufficient candle history to compute *any* indicator (a freshly
    /// installed bot before the first warm-up bars land).
    /// </summary>
    Task<IndicatorSnapshot?> GetSnapshotAsync(
        int symbolId,
        string interval,
        DateTime asOf,
        CancellationToken cancellationToken);

    /// <summary>
    /// Higher-timeframe alignment helper. Functionally identical to
    /// <see cref="GetSnapshotAsync"/> with <c>"4h"</c>; exists as a named method
    /// because the trend strategy spec (§3.3) names HTF alignment as a first-class
    /// concept and the call sites read more clearly that way.
    /// </summary>
    Task<IndicatorSnapshot?> GetHtfSnapshotAsync(
        int symbolId,
        string htfInterval,
        DateTime asOf,
        CancellationToken cancellationToken);
}
