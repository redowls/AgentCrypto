using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface ISignalRepository
{
    Task<long> InsertAsync(Signal signal, CancellationToken cancellationToken);

    Task<Signal?> GetByIdAsync(long signalId, CancellationToken cancellationToken);

    Task<int> UpdateStatusAsync(long signalId, string status, string? reason, CancellationToken cancellationToken);

    Task<IReadOnlyList<Signal>> GetByStatusAsync(string status, int top, CancellationToken cancellationToken);
}
