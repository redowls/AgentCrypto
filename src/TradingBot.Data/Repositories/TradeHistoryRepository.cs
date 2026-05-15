using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class TradeHistoryRepository(IDbConnectionFactory connectionFactory) : ITradeHistoryRepository
{
    static TradeHistoryRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(TradeHistory trade, CancellationToken cancellationToken)
    {
        // Property RMultiple ↔ column R_Multiple via Dapper underscore matching.
        const string sql = """
            INSERT INTO dbo.TradeHistory
                (PositionId, SymbolId, Strategy, Side, EntryTime, ExitTime,
                 HoldingMinutes, EntryPrice, ExitPrice, Quantity,
                 GrossPnlUsd, FeesUsd, NetPnlUsd, R_Multiple, ExitReason)
            OUTPUT INSERTED.TradeHistoryId
            VALUES
                (@PositionId, @SymbolId, @Strategy, @Side, @EntryTime, @ExitTime,
                 @HoldingMinutes, @EntryPrice, @ExitPrice, @Quantity,
                 @GrossPnlUsd, @FeesUsd, @NetPnlUsd, @RMultiple, @ExitReason);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, trade, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        trade.TradeHistoryId = id;
        return id;
    }

    public async Task<IReadOnlyList<TradeHistory>> GetByStrategyAsync(
        string strategy,
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TradeHistoryId, PositionId, SymbolId, Strategy, Side,
                   EntryTime, ExitTime, HoldingMinutes,
                   EntryPrice, ExitPrice, Quantity,
                   GrossPnlUsd, FeesUsd, NetPnlUsd, R_Multiple AS RMultiple, ExitReason
            FROM   dbo.TradeHistory
            WHERE  Strategy = @Strategy AND ExitTime >= @From AND ExitTime < @To
            ORDER BY ExitTime DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TradeHistory>(
            new CommandDefinition(sql,
                new { Strategy = strategy, From = fromUtcInclusive, To = toUtcExclusive },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TradeHistory>> GetInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        int      take,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(@Take) TradeHistoryId, PositionId, SymbolId, Strategy, Side,
                   EntryTime, ExitTime, HoldingMinutes,
                   EntryPrice, ExitPrice, Quantity,
                   GrossPnlUsd, FeesUsd, NetPnlUsd, R_Multiple AS RMultiple, ExitReason
            FROM   dbo.TradeHistory
            WHERE  ExitTime >= @From AND ExitTime < @To
            ORDER BY ExitTime DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TradeHistory>(
            new CommandDefinition(sql,
                new { From = fromUtcInclusive, To = toUtcExclusive, Take = take },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
