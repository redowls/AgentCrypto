using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class FillRepository(IDbConnectionFactory connectionFactory) : IFillRepository
{
    static FillRepository() => DapperBootstrap.EnsureInitialised();

    // INSERT ... WHERE NOT EXISTS by (OrderId, TradeId). The unique constraint
    // is the ultimate guard, but the pre-check avoids constraint-violation
    // exceptions on routine retries from the user-data-stream consumer.
    public async Task<bool> InsertIfNewAsync(Fill fill, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Fills
                (OrderId, TradeId, Quantity, Price, Commission, CommissionAsset, IsMaker, TradeTime)
            SELECT @OrderId, @TradeId, @Quantity, @Price, @Commission, @CommissionAsset, @IsMaker, @TradeTime
            WHERE NOT EXISTS
                (SELECT 1 FROM dbo.Fills WHERE OrderId = @OrderId AND TradeId = @TradeId);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, fill, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Fill>> GetByOrderAsync(long orderId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FillId, OrderId, TradeId, Quantity, Price, Commission, CommissionAsset, IsMaker, TradeTime
            FROM   dbo.Fills
            WHERE  OrderId = @OrderId
            ORDER BY TradeTime ASC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Fill>(
            new CommandDefinition(sql, new { OrderId = orderId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
