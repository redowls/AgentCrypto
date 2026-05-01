using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface ITradeHistoryRepository
{
    Task<long> InsertAsync(TradeHistory trade, CancellationToken cancellationToken);

    Task<IReadOnlyList<TradeHistory>> GetByStrategyAsync(
        string strategy,
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken);
}
