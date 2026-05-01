using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface ICandleRepository
{
    /// <summary>
    /// Idempotent upsert by (SymbolId, Interval, OpenTime). If a row already exists
    /// with that natural key, fields are updated only when IsClosed transitions or
    /// values change (last-write-wins on the closing tick).
    /// </summary>
    Task<int> UpsertAsync(Candle candle, CancellationToken cancellationToken);

    /// <summary>
    /// Bulk upsert via SqlBulkCopy → staging → MERGE. Idempotent; safe to retry.
    /// Returns the number of rows affected by the MERGE (inserted + updated).
    /// </summary>
    Task<int> BulkUpsertAsync(IReadOnlyCollection<Candle> candles, CancellationToken cancellationToken);

    Task<Candle?> GetAsync(int symbolId, string interval, DateTime openTime, CancellationToken cancellationToken);

    Task<IReadOnlyList<Candle>> GetRangeAsync(
        int symbolId,
        string interval,
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken);

    Task<DateTime?> GetLatestOpenTimeAsync(int symbolId, string interval, CancellationToken cancellationToken);
}
