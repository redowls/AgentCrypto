using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class PositionRepository(IDbConnectionFactory connectionFactory) : IPositionRepository
{
    static PositionRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(Position position, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Positions
                (SymbolId, AccountType, Side, EntrySignalId, EntryOrderId,
                 Quantity, AvgEntryPrice, StopLoss, TakeProfit, InitialRiskUsd,
                 OpenedAt, Status)
            OUTPUT INSERTED.PositionId
            VALUES
                (@SymbolId, @AccountType, @Side, @EntrySignalId, @EntryOrderId,
                 @Quantity, @AvgEntryPrice, @StopLoss, @TakeProfit, @InitialRiskUsd,
                 @OpenedAt, @Status);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, position, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        position.PositionId = id;
        return id;
    }

    public async Task<Position?> GetByIdAsync(long positionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT PositionId, SymbolId, AccountType, Side, EntrySignalId, EntryOrderId,
                   Quantity, AvgEntryPrice, StopLoss, TakeProfit, InitialRiskUsd,
                   OpenedAt, ClosedAt, ClosePrice, RealizedPnlUsd, Status
            FROM   dbo.Positions
            WHERE  PositionId = @PositionId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Position>(
            new CommandDefinition(sql, new { PositionId = positionId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<Position?> GetOpenForSymbolAsync(int symbolId, string accountType, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) PositionId, SymbolId, AccountType, Side, EntrySignalId, EntryOrderId,
                   Quantity, AvgEntryPrice, StopLoss, TakeProfit, InitialRiskUsd,
                   OpenedAt, ClosedAt, ClosePrice, RealizedPnlUsd, Status
            FROM   dbo.Positions
            WHERE  SymbolId = @SymbolId AND AccountType = @AccountType
              AND  Status IN ('OPEN','CLOSING')
            ORDER BY OpenedAt DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Position>(
            new CommandDefinition(sql, new { SymbolId = symbolId, AccountType = accountType },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Position>> GetOpenAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT PositionId, SymbolId, AccountType, Side, EntrySignalId, EntryOrderId,
                   Quantity, AvgEntryPrice, StopLoss, TakeProfit, InitialRiskUsd,
                   OpenedAt, ClosedAt, ClosePrice, RealizedPnlUsd, Status
            FROM   dbo.Positions
            WHERE  Status IN ('OPEN','CLOSING')
            ORDER BY OpenedAt ASC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Position>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<int> UpdateStopsAsync(
        long positionId,
        decimal stopLoss,
        decimal takeProfit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Positions
            SET    StopLoss   = @StopLoss,
                   TakeProfit = @TakeProfit
            WHERE  PositionId = @PositionId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { PositionId = positionId, StopLoss = stopLoss, TakeProfit = takeProfit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ExtendAsync(
        long positionId,
        decimal addedQuantity,
        decimal addedFillPrice,
        CancellationToken cancellationToken)
    {
        // Weighted-average entry price recomputed in-place. Only OPEN
        // positions can be extended; CLOSING/CLOSED rows are no-ops.
        const string sql = """
            UPDATE dbo.Positions
            SET    AvgEntryPrice = ((Quantity * AvgEntryPrice) + (@Qty * @Px)) /
                                   NULLIF(Quantity + @Qty, 0),
                   Quantity      = Quantity + @Qty
            WHERE  PositionId = @PositionId AND Status = 'OPEN';
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            PositionId = positionId,
            Qty        = addedQuantity,
            Px         = addedFillPrice,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ReduceQuantityAsync(
        long positionId,
        decimal removedQuantity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Positions
            SET    Quantity = CASE
                                WHEN Quantity - @Qty < 0 THEN 0
                                ELSE Quantity - @Qty
                              END
            WHERE  PositionId = @PositionId AND Status IN ('OPEN','CLOSING');
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            PositionId = positionId,
            Qty        = removedQuantity,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CloseAsync(
        long positionId,
        DateTime closedAtUtc,
        decimal closePrice,
        decimal realizedPnlUsd,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Positions
            SET    ClosedAt       = @ClosedAt,
                   ClosePrice     = @ClosePrice,
                   RealizedPnlUsd = @RealizedPnlUsd,
                   Status         = 'CLOSED'
            WHERE  PositionId = @PositionId
              AND  Status IN ('OPEN','CLOSING');
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            PositionId     = positionId,
            ClosedAt       = closedAtUtc,
            ClosePrice     = closePrice,
            RealizedPnlUsd = realizedPnlUsd,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
