using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IFillRepository
{
    /// <summary>
    /// Idempotent insert by (OrderId, TradeId). Returns true if a new row was inserted.
    /// </summary>
    Task<bool> InsertIfNewAsync(Fill fill, CancellationToken cancellationToken);

    Task<IReadOnlyList<Fill>> GetByOrderAsync(long orderId, CancellationToken cancellationToken);
}
