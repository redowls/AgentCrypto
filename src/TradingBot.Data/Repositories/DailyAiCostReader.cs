using Dapper;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class DailyAiCostReader : IDailyAiCostReader
{
    private readonly IDbConnectionFactory _connectionFactory;

    static DailyAiCostReader() => DapperBootstrap.EnsureInitialised();

    public DailyAiCostReader(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<decimal> GetTotalForDayAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT COALESCE(SUM(CostUsd), 0)
FROM   dbo.AiInteractions
WHERE  CreatedAt >= @startUtc AND CreatedAt < @endUtc;";

        await using var conn = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(sql, new { startUtc, endUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
