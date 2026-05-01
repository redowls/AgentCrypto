using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class OrderRepository(IDbConnectionFactory connectionFactory) : IOrderRepository
{
    static OrderRepository() => DapperBootstrap.EnsureInitialised();

    // Idempotent insert keyed on ClientOrderId. Returns the existing OrderId
    // if one already exists, otherwise inserts and returns the new identity.
    // Uses a single round-trip with COALESCE on a pre-check (cheaper than
    // catching the unique constraint violation, since duplicates are common
    // on retry in this flow).
    public async Task<long> InsertIfNewAsync(Order order, CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @ExistingId BIGINT =
                (SELECT OrderId FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK)
                 WHERE  ClientOrderId = @ClientOrderId);

            IF @ExistingId IS NOT NULL
            BEGIN
                SELECT @ExistingId;
                RETURN;
            END

            INSERT INTO dbo.Orders
                (SignalId, SymbolId, AccountType, ClientOrderId, ExchangeOrderId,
                 OrderType, Side, PositionSide, Quantity, Price, StopPrice,
                 TimeInForce, ReduceOnly, Status, FilledQty, AvgFillPrice,
                 CommissionPaid, CommissionAsset, Notes)
            OUTPUT INSERTED.OrderId
            VALUES
                (@SignalId, @SymbolId, @AccountType, @ClientOrderId, @ExchangeOrderId,
                 @OrderType, @Side, @PositionSide, @Quantity, @Price, @StopPrice,
                 @TimeInForce, @ReduceOnly, @Status, @FilledQty, @AvgFillPrice,
                 @CommissionPaid, @CommissionAsset, @Notes);
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, order, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        order.OrderId = id;
        return id;
    }

    public async Task<Order?> GetByIdAsync(long orderId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OrderId, SignalId, SymbolId, AccountType, ClientOrderId, ExchangeOrderId,
                   OrderType, Side, PositionSide, Quantity, Price, StopPrice,
                   TimeInForce, ReduceOnly, Status, FilledQty, AvgFillPrice,
                   CommissionPaid, CommissionAsset, SubmittedAt, LastUpdatedAt, Notes
            FROM   dbo.Orders
            WHERE  OrderId = @OrderId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Order>(
            new CommandDefinition(sql, new { OrderId = orderId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<Order?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OrderId, SignalId, SymbolId, AccountType, ClientOrderId, ExchangeOrderId,
                   OrderType, Side, PositionSide, Quantity, Price, StopPrice,
                   TimeInForce, ReduceOnly, Status, FilledQty, AvgFillPrice,
                   CommissionPaid, CommissionAsset, SubmittedAt, LastUpdatedAt, Notes
            FROM   dbo.Orders
            WHERE  ClientOrderId = @ClientOrderId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Order>(
            new CommandDefinition(sql, new { ClientOrderId = clientOrderId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> UpdateStatusAsync(
        long orderId,
        string status,
        decimal filledQty,
        decimal? avgFillPrice,
        decimal commissionPaid,
        string? commissionAsset,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Orders
            SET    Status          = @Status,
                   FilledQty       = @FilledQty,
                   AvgFillPrice    = @AvgFillPrice,
                   CommissionPaid  = @CommissionPaid,
                   CommissionAsset = @CommissionAsset,
                   LastUpdatedAt   = SYSUTCDATETIME()
            WHERE  OrderId = @OrderId;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrderId         = orderId,
            Status          = status,
            FilledQty       = filledQty,
            AvgFillPrice    = avgFillPrice,
            CommissionPaid  = commissionPaid,
            CommissionAsset = commissionAsset,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Order>> GetOpenAsync(int symbolId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OrderId, SignalId, SymbolId, AccountType, ClientOrderId, ExchangeOrderId,
                   OrderType, Side, PositionSide, Quantity, Price, StopPrice,
                   TimeInForce, ReduceOnly, Status, FilledQty, AvgFillPrice,
                   CommissionPaid, CommissionAsset, SubmittedAt, LastUpdatedAt, Notes
            FROM   dbo.Orders
            WHERE  SymbolId = @SymbolId
              AND  Status IN ('NEW','PARTIALLY_FILLED');
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Order>(
            new CommandDefinition(sql, new { SymbolId = symbolId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
