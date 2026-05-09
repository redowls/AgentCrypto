using Dapper;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class AlertJournalRepository(IDbConnectionFactory connectionFactory) : IAlertJournalRepository
{
    static AlertJournalRepository() => DapperBootstrap.EnsureInitialised();

    public async Task InsertAsync(AlertJournalRow row, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.AlertJournal
                (SentAtUtc, Severity, Title, Body, Fingerprint, Transports, InstanceId, CorrelationId)
            VALUES
                (@SentAtUtc, @Severity, @Title, @Body, @Fingerprint, @Transports, @InstanceId, @CorrelationId);
        """;

        await using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertJournalRow>> GetWindowAsync(
        byte? severity,
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken ct)
    {
        const string sql = """
            SELECT Id, SentAtUtc, Severity, Title, Body, Fingerprint, Transports, InstanceId, CorrelationId
            FROM   dbo.AlertJournal
            WHERE  SentAtUtc >= @SinceUtc AND SentAtUtc < @UntilUtc
              AND  (@Severity IS NULL OR Severity = @Severity)
            ORDER  BY SentAtUtc;
        """;

        await using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AlertJournalRow>(
            new CommandDefinition(sql, new
            {
                SinceUtc = sinceUtc,
                UntilUtc = untilUtc,
                Severity = severity,
            }, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
