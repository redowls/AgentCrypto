using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class RiskEventRepository(IDbConnectionFactory connectionFactory) : IRiskEventRepository
{
    static RiskEventRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(RiskEvent riskEvent, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.RiskEvents
                (EventType, Severity, SymbolId, SignalId, OrderId, Payload, Acted)
            OUTPUT INSERTED.RiskEventId
            VALUES
                (@EventType, @Severity, @SymbolId, @SignalId, @OrderId, @Payload, @Acted);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, riskEvent, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        riskEvent.RiskEventId = id;
        return id;
    }

    public async Task<IReadOnlyList<RiskEvent>> GetRecentAsync(string eventType, int top, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(@Top)
                   RiskEventId, EventTime, EventType, Severity,
                   SymbolId, SignalId, OrderId, Payload, Acted
            FROM   dbo.RiskEvents
            WHERE  EventType = @EventType
            ORDER BY EventTime DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RiskEvent>(
            new CommandDefinition(sql, new { EventType = eventType, Top = top },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
