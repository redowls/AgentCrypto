using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class RegimeRepository(IDbConnectionFactory connectionFactory) : IRegimeRepository
{
    static RegimeRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(RegimeRecord record, CancellationToken cancellationToken)
    {
        // Idempotent on the natural key (SymbolId, Interval, AsOf, Source) — a
        // double-fire on the bar-close path returns the existing RegimeId
        // rather than throwing on the UNIQUE constraint. We OUTPUT the inserted
        // id when MERGE inserts, and re-SELECT it on the no-op branch.
        const string sql = """
            DECLARE @ids TABLE (RegimeId BIGINT);

            MERGE dbo.Regimes WITH (HOLDLOCK) AS tgt
            USING (SELECT @SymbolId AS SymbolId,
                          @Interval AS [Interval],
                          @AsOf     AS AsOf,
                          @Regime   AS Regime,
                          @Confidence AS Confidence,
                          @Source   AS Source,
                          @Inputs   AS Inputs) AS src
                ON tgt.SymbolId  = src.SymbolId
               AND tgt.[Interval] = src.[Interval]
               AND tgt.AsOf      = src.AsOf
               AND tgt.Source    = src.Source
            WHEN NOT MATCHED BY TARGET THEN INSERT
                (SymbolId, [Interval], AsOf, Regime, Confidence, Source, Inputs)
                VALUES
                (src.SymbolId, src.[Interval], src.AsOf, src.Regime, src.Confidence, src.Source, src.Inputs)
            OUTPUT INSERTED.RegimeId INTO @ids;

            SELECT TOP(1) RegimeId FROM @ids
            UNION ALL
            SELECT RegimeId FROM dbo.Regimes
            WHERE  SymbolId = @SymbolId
              AND  [Interval] = @Interval
              AND  AsOf = @AsOf
              AND  Source = @Source;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, record, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        record.RegimeId = id;
        return id;
    }

    public async Task<RegimeRecord?> GetLatestAsync(int symbolId, string interval, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1)
                   RegimeId, SymbolId, [Interval], AsOf, Regime, Confidence, Source, Inputs, CreatedAt
            FROM   dbo.Regimes
            WHERE  SymbolId = @SymbolId AND [Interval] = @Interval
            ORDER BY AsOf DESC, RegimeId DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<RegimeRecord>(
            new CommandDefinition(sql, new { SymbolId = symbolId, Interval = interval },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
