using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class CorrelationRepository(IDbConnectionFactory connectionFactory) : ICorrelationRepository
{
    static CorrelationRepository() => DapperBootstrap.EnsureInitialised();

    public async Task ReplaceSnapshotAsync(
        DateTime asOf,
        IReadOnlyList<CorrelationPair> pairs,
        IReadOnlyList<CorrelationCluster> clusters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(clusters);

        await using var conn = (SqlConnection)await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent re-runs: blow away anything previously written for this AsOf.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Correlations WHERE AsOf = @AsOf;",
            new { AsOf = asOf }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.CorrelationClusters WHERE AsOf = @AsOf;",
            new { AsOf = asOf }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (pairs.Count > 0)
        {
            const string insertPair = """
                INSERT INTO dbo.Correlations
                    (AsOf, SymbolIdA, SymbolIdB, LookbackDays, Correlation, SampleCount)
                VALUES
                    (@AsOf, @SymbolIdA, @SymbolIdB, @LookbackDays, @Correlation, @SampleCount);
            """;
            await conn.ExecuteAsync(new CommandDefinition(
                insertPair, pairs, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (clusters.Count > 0)
        {
            const string insertCluster = """
                INSERT INTO dbo.CorrelationClusters
                    (AsOf, SymbolId, Cluster, Threshold)
                VALUES
                    (@AsOf, @SymbolId, @Cluster, @Threshold);
            """;
            await conn.ExecuteAsync(new CommandDefinition(
                insertCluster, clusters, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTime?> GetLatestAsOfAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT MAX(AsOf) FROM dbo.Correlations;";
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int?> GetClusterAsync(DateTime asOf, int symbolId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Cluster FROM dbo.CorrelationClusters
            WHERE AsOf = @AsOf AND SymbolId = @SymbolId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(sql, new { AsOf = asOf, SymbolId = symbolId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> GetClusterMembersAsync(DateTime asOf, int cluster, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT SymbolId FROM dbo.CorrelationClusters
            WHERE AsOf = @AsOf AND Cluster = @Cluster;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<int>(
            new CommandDefinition(sql, new { AsOf = asOf, Cluster = cluster }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.ToList();
    }
}
