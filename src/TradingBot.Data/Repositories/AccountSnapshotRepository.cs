using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class AccountSnapshotRepository(IDbConnectionFactory connectionFactory) : IAccountSnapshotRepository
{
    static AccountSnapshotRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(AccountSnapshot snapshot, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.AccountSnapshots
                (AccountType, SnapshotTime, EquityUsd, AvailableUsd, UnrealizedPnl,
                 OpenPositions, GrossExposure, NetExposure, Drawdown)
            OUTPUT INSERTED.SnapshotId
            VALUES
                (@AccountType, @SnapshotTime, @EquityUsd, @AvailableUsd, @UnrealizedPnl,
                 @OpenPositions, @GrossExposure, @NetExposure, @Drawdown);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, snapshot, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        snapshot.SnapshotId = id;
        return id;
    }

    public async Task<AccountSnapshot?> GetLatestAsync(string accountType, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) SnapshotId, AccountType, SnapshotTime, EquityUsd, AvailableUsd,
                          UnrealizedPnl, OpenPositions, GrossExposure, NetExposure, Drawdown
            FROM   dbo.AccountSnapshots
            WHERE  AccountType = @AccountType
            ORDER BY SnapshotTime DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<AccountSnapshot>(
            new CommandDefinition(sql, new { AccountType = accountType }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
