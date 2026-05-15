using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IAccountSnapshotRepository
{
    Task<long> InsertAsync(AccountSnapshot snapshot, CancellationToken cancellationToken);

    Task<AccountSnapshot?> GetLatestAsync(string accountType, CancellationToken cancellationToken);

    /// All-time high-water-mark equity for the account, used by the §8.4
    /// drawdown ladder. Null when the table is empty.
    Task<decimal?> GetMaxEquityAsync(string accountType, CancellationToken cancellationToken);

    /// First snapshot at or after the supplied UTC instant. Used by the
    /// §8.2 daily-loss-limit gate to anchor "since 00:00 UTC". Null when no
    /// snapshot has been written yet today.
    Task<AccountSnapshot?> GetFirstAtOrAfterAsync(
        string accountType,
        DateTime fromUtcInclusive,
        CancellationToken cancellationToken);
}
