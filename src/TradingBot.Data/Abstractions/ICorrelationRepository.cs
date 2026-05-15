using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

/// Persistence for §8.3 correlation matrix + cluster assignments. Reads are
/// served from the most-recent <c>AsOf</c>; writes happen in a single batch
/// by the nightly <c>CorrelationRefreshJob</c>.
public interface ICorrelationRepository
{
    /// Replaces the entire matrix + cluster snapshot for a single AsOf in one
    /// transaction. The job builds the rows in memory (universe is &lt;100
    /// symbols) and hands them to this method.
    Task ReplaceSnapshotAsync(
        DateTime asOf,
        IReadOnlyList<CorrelationPair> pairs,
        IReadOnlyList<CorrelationCluster> clusters,
        CancellationToken cancellationToken);

    /// Latest AsOf present in <c>dbo.Correlations</c>; null when the table is
    /// empty (e.g. fresh deployment before the first nightly run).
    Task<DateTime?> GetLatestAsOfAsync(CancellationToken cancellationToken);

    /// Cluster index assigned to <paramref name="symbolId"/> at <paramref name="asOf"/>.
    /// Returns null when the symbol was not part of the snapshot's universe.
    Task<int?> GetClusterAsync(DateTime asOf, int symbolId, CancellationToken cancellationToken);

    /// All symbols sharing a cluster at <paramref name="asOf"/>.
    Task<IReadOnlyList<int>> GetClusterMembersAsync(DateTime asOf, int cluster, CancellationToken cancellationToken);
}
