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

    /// Stamps the exchange-side identifier onto a previously-PENDING row and
    /// transitions it to <paramref name="newStatus"/> (typically NEW or
    /// SUBMITTING → NEW). The caller is responsible for state-machine
    /// validation; this method is the persistence side of an already-checked
    /// transition.
    Task<int> SetExchangeOrderIdAsync(
        long orderId,
        long exchangeOrderId,
        string newStatus,
        CancellationToken cancellationToken);

    /// Pure status update used for state transitions that do not change fill
    /// quantities (e.g. PENDING→SUBMITTING, NEW→CANCELING, →ERROR).
    Task<int> UpdateStatusOnlyAsync(
        long orderId,
        string newStatus,
        string? notes,
        CancellationToken cancellationToken);

    /// Reconciliation candidates: every order in a non-terminal state where
    /// LastUpdatedAt is older than <paramref name="olderThanUtc"/>. Returns
    /// at most <paramref name="maxRows"/> rows ordered by LastUpdatedAt ASC.
    Task<IReadOnlyList<Order>> GetNonTerminalOlderThanAsync(
        DateTime olderThanUtc,
        int maxRows,
        CancellationToken cancellationToken);
}
