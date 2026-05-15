using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class AiJournalRepository(IDbConnectionFactory connectionFactory) : IAiJournalRepository
{
    static AiJournalRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> UpsertAsync(AiJournalRecord row, CancellationToken cancellationToken)
    {
        // Idempotent on (IsoYear, IsoWeek). On a re-run for the same week
        // (e.g. the operator re-triggers the journal job manually) we OVERWRITE
        // the existing row's markdown — the latest run is the source of truth.
        const string sql = """
            DECLARE @ids TABLE (AiJournalId BIGINT);

            MERGE dbo.AiJournals WITH (HOLDLOCK) AS tgt
            USING (SELECT @IsoYear AS IsoYear, @IsoWeek AS IsoWeek) AS src
                ON tgt.IsoYear = src.IsoYear AND tgt.IsoWeek = src.IsoWeek
            WHEN MATCHED THEN UPDATE SET
                PeriodStartUtc  = @PeriodStartUtc,
                PeriodEndUtc    = @PeriodEndUtc,
                TradesAnalyzed  = @TradesAnalyzed,
                Markdown        = @Markdown,
                AiInteractionId = @AiInteractionId
            WHEN NOT MATCHED BY TARGET THEN INSERT
                (IsoYear, IsoWeek, PeriodStartUtc, PeriodEndUtc, TradesAnalyzed,
                 Markdown, AiInteractionId)
                VALUES
                (@IsoYear, @IsoWeek, @PeriodStartUtc, @PeriodEndUtc, @TradesAnalyzed,
                 @Markdown, @AiInteractionId)
            OUTPUT INSERTED.AiJournalId INTO @ids;

            SELECT TOP(1) AiJournalId FROM @ids
            UNION ALL
            SELECT AiJournalId FROM dbo.AiJournals
            WHERE  IsoYear = @IsoYear AND IsoWeek = @IsoWeek;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        row.AiJournalId = id;
        return id;
    }

    public async Task<AiJournalRecord?> GetByIsoWeekAsync(int isoYear, int isoWeek, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) AiJournalId, IsoYear, IsoWeek, PeriodStartUtc, PeriodEndUtc,
                          TradesAnalyzed, Markdown, AiInteractionId, CreatedAt
            FROM   dbo.AiJournals
            WHERE  IsoYear = @IsoYear AND IsoWeek = @IsoWeek;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<AiJournalRecord>(
            new CommandDefinition(sql, new { IsoYear = isoYear, IsoWeek = isoWeek },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
