using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IOrderRepository
{
    /// <summary>
    /// Idempotent insert keyed on ClientOrderId. If a row with the same
    /// ClientOrderId already exists, returns its OrderId without inserting.
    /// </summary>
    Task<long> InsertIfNewAsync(Order order, CancellationToken cancellationToken);

    Task<Order?> GetByIdAsync(long orderId, CancellationToken cancellationToken);

    Task<Order?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    Task<int> UpdateStatusAsync(
        long orderId,
        string status,
        decimal filledQty,
        decimal? avgFillPrice,
        decimal commissionPaid,
        string? commissionAsset,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> GetOpenAsync(int symbolId, CancellationToken cancellationToken);
}
