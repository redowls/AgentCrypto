using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class SymbolRepository(IDbConnectionFactory connectionFactory) : ISymbolRepository
{
    static SymbolRepository() => DapperBootstrap.EnsureInitialised();

    // SQL column `Symbol` is aliased to `SymbolCode` so Dapper maps it to the
    // C# property of the same name (the class itself is `Symbol`).
    private const string SelectColumns = """
        SELECT SymbolId, Exchange, Symbol AS SymbolCode, BaseAsset, QuoteAsset,
               TickSize, StepSize, MinNotional, IsActive, UpdatedAt
        """;

    public async Task<Symbol?> GetByIdAsync(int symbolId, CancellationToken cancellationToken)
    {
        var sql = $"{SelectColumns} FROM dbo.Symbols WHERE SymbolId = @SymbolId;";
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Symbol>(
            new CommandDefinition(sql, new { SymbolId = symbolId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<Symbol?> GetByExchangeAndCodeAsync(string exchange, string symbol, CancellationToken cancellationToken)
    {
        var sql = $"{SelectColumns} FROM dbo.Symbols WHERE Exchange = @Exchange AND Symbol = @Symbol;";
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Symbol>(
            new CommandDefinition(sql, new { Exchange = exchange, Symbol = symbol }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Symbol>> GetActiveAsync(CancellationToken cancellationToken)
    {
        var sql = $"{SelectColumns} FROM dbo.Symbols WHERE IsActive = 1 ORDER BY Exchange, Symbol;";
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Symbol>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Symbol>> GetByExchangeAsync(string exchange, CancellationToken cancellationToken)
    {
        var sql = $"{SelectColumns} FROM dbo.Symbols WHERE Exchange = @Exchange ORDER BY Symbol;";
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Symbol>(
            new CommandDefinition(sql, new { Exchange = exchange }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<SymbolUpsertCounts> UpsertExchangeCatalogAsync(
        string exchange,
        IReadOnlyList<Symbol> rows,
        CancellationToken cancellationToken)
    {
        // MERGE in a single transaction. The OUTPUT clause distinguishes
        // INSERT vs UPDATE so the caller can report counts without an extra
        // round-trip. Symbols absent from the supplied set are deactivated
        // (IsActive = 0) rather than deleted, preserving FK integrity from
        // Orders / Positions / Candles.
        const string mergeSql = """
            MERGE dbo.Symbols AS tgt
            USING (
                SELECT @Exchange AS Exchange, @SymbolCode AS Symbol, @BaseAsset AS BaseAsset,
                       @QuoteAsset AS QuoteAsset, @TickSize AS TickSize, @StepSize AS StepSize,
                       @MinNotional AS MinNotional, @IsActive AS IsActive, @UpdatedAt AS UpdatedAt
            ) AS src
              ON tgt.Exchange = src.Exchange AND tgt.Symbol = src.Symbol
            WHEN MATCHED THEN
              UPDATE SET BaseAsset   = src.BaseAsset,
                         QuoteAsset  = src.QuoteAsset,
                         TickSize    = src.TickSize,
                         StepSize    = src.StepSize,
                         MinNotional = src.MinNotional,
                         IsActive    = src.IsActive,
                         UpdatedAt   = src.UpdatedAt
            WHEN NOT MATCHED THEN
              INSERT (Exchange, Symbol, BaseAsset, QuoteAsset, TickSize, StepSize, MinNotional, IsActive, UpdatedAt)
              VALUES (src.Exchange, src.Symbol, src.BaseAsset, src.QuoteAsset, src.TickSize, src.StepSize, src.MinNotional, src.IsActive, src.UpdatedAt)
            OUTPUT $action AS Action;
        """;

        const string deactivateSql = """
            UPDATE dbo.Symbols
            SET    IsActive  = 0,
                   UpdatedAt = @UpdatedAt
            WHERE  Exchange  = @Exchange
              AND  IsActive  = 1
              AND  Symbol NOT IN @Codes;
        """;

        var now = DateTime.UtcNow;
        var inserted = 0;
        var updated  = 0;
        var deactivated = 0;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var actions = await conn.QueryAsync<string>(
                new CommandDefinition(mergeSql, new
                {
                    Exchange    = exchange,
                    row.SymbolCode,
                    row.BaseAsset,
                    row.QuoteAsset,
                    row.TickSize,
                    row.StepSize,
                    row.MinNotional,
                    row.IsActive,
                    UpdatedAt = now,
                }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (var action in actions)
            {
                if (string.Equals(action, "INSERT", StringComparison.OrdinalIgnoreCase)) inserted++;
                else if (string.Equals(action, "UPDATE", StringComparison.OrdinalIgnoreCase)) updated++;
            }
        }

        if (rows.Count > 0)
        {
            var codes = rows.Select(r => r.SymbolCode).ToArray();
            deactivated = await conn.ExecuteAsync(
                new CommandDefinition(deactivateSql, new
                {
                    Exchange = exchange,
                    UpdatedAt = now,
                    Codes = codes,
                }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SymbolUpsertCounts(inserted, updated, deactivated);
    }
}
