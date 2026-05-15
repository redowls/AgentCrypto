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

    /// <summary>
    /// All closed trades with <c>ExitTime</c> in the half-open
    /// [fromUtc, toUtc) window. Used by the §5.4.4 weekly journal job.
    /// </summary>
    Task<IReadOnlyList<TradeHistory>> GetInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        int      take,
        CancellationToken cancellationToken);
}
