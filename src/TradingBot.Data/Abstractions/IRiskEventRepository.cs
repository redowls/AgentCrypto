using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IRiskEventRepository
{
    Task<long> InsertAsync(RiskEvent riskEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<RiskEvent>> GetRecentAsync(string eventType, int top, CancellationToken cancellationToken);
}
