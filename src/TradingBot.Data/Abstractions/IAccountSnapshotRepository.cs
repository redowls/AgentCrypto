using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IAccountSnapshotRepository
{
    Task<long> InsertAsync(AccountSnapshot snapshot, CancellationToken cancellationToken);

    Task<AccountSnapshot?> GetLatestAsync(string accountType, CancellationToken cancellationToken);
}
