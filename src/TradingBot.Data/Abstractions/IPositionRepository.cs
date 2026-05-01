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
}
