using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public sealed record SymbolUpsertCounts(int Inserted, int Updated, int Deactivated);

public interface ISymbolRepository
{
    Task<Symbol?> GetByIdAsync(int symbolId, CancellationToken cancellationToken);

    Task<Symbol?> GetByExchangeAndCodeAsync(string exchange, string symbol, CancellationToken cancellationToken);

    Task<IReadOnlyList<Symbol>> GetActiveAsync(CancellationToken cancellationToken);

    /// Upsert one exchange's symbol catalogue. Symbols absent from
    /// <paramref name="rows"/> but present in the DB for that exchange are
    /// marked <c>IsActive = 0</c> rather than deleted.
    Task<SymbolUpsertCounts> UpsertExchangeCatalogAsync(
        string exchange,
        IReadOnlyList<Symbol> rows,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Symbol>> GetByExchangeAsync(string exchange, CancellationToken cancellationToken);
}
