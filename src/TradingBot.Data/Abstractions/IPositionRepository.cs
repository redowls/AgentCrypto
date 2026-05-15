using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IPositionRepository
{
    Task<long> InsertAsync(Position position, CancellationToken cancellationToken);

    Task<Position?> GetByIdAsync(long positionId, CancellationToken cancellationToken);

    Task<Position?> GetOpenForSymbolAsync(int symbolId, string accountType, CancellationToken cancellationToken);

    Task<IReadOnlyList<Position>> GetOpenAsync(CancellationToken cancellationToken);

    Task<int> UpdateStopsAsync(
        long positionId,
        decimal stopLoss,
        decimal takeProfit,
        CancellationToken cancellationToken);

    Task<int> CloseAsync(
        long positionId,
        DateTime closedAtUtc,
        decimal closePrice,
        decimal realizedPnlUsd,
        CancellationToken cancellationToken);

    /// Increment quantity and recompute weighted-average entry price for an
    /// existing OPEN position when an additional fill arrives (averaging-in
    /// or pyramiding scenarios). Returns the row count touched.
    Task<int> ExtendAsync(
        long positionId,
        decimal addedQuantity,
        decimal addedFillPrice,
        CancellationToken cancellationToken);

    /// Reduce open quantity by an exit-fill quantity. When the resulting
    /// quantity is ≤ 0 the row is closed via <see cref="CloseAsync"/> by the
    /// caller; this method only debits the qty and exposes the new value.
    Task<int> ReduceQuantityAsync(
        long positionId,
        decimal removedQuantity,
        CancellationToken cancellationToken);
}
