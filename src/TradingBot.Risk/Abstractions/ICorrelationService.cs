using TradingBot.Core.Domain;

namespace TradingBot.Risk.Abstractions;

/// <summary>
/// §8.3 — read side of the correlation gate. Backed by
/// <c>dbo.CorrelationClusters</c>; the nightly Quartz job maintains the table.
///
/// "Cluster occupied on the same side" means: for the symbol being entered,
/// look up its cluster at the latest <c>AsOf</c>; if any open position belongs
/// to that same cluster on the same side, reject. Stablecoins / shorts form
/// their own clusters per §8.3 — implementations must respect side parity.
/// </summary>
public interface ICorrelationService
{
    /// True when at least one position in <paramref name="openPositions"/> is in
    /// the same cluster as <paramref name="symbolId"/> on the same
    /// <paramref name="side"/>. Falls back to <c>false</c> when no correlation
    /// snapshot exists yet (e.g. fresh deployment) — the gate does not block in
    /// that case; failsafe is permissive because the other gates still apply.
    /// </summary>
    Task<bool> IsClusterOccupiedAsync(
        int symbolId,
        string side,
        IReadOnlyCollection<Position> openPositions,
        CancellationToken cancellationToken);
}

/// <summary>
/// Write side of the correlation gate — runs nightly, builds the matrix from
/// <c>dbo.Candles</c> and persists pairs + cluster assignments.
/// </summary>
public interface ICorrelationRefresher
{
    Task RefreshAsync(DateTime asOf, CancellationToken cancellationToken);
}
