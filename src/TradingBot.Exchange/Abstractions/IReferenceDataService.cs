using TradingBot.Core.Domain;

namespace TradingBot.Exchange.Abstractions;

public sealed record ReferenceDataRefreshResult(
    AccountType Account,
    int         Inserted,
    int         Updated,
    int         Deactivated,
    DateTime    AtUtc);

public interface IReferenceDataService
{
    /// Fetch exchangeInfo for both spot and futures and persist to dbo.Symbols.
    /// Called on startup and daily by the hosted background service.
    Task<IReadOnlyList<ReferenceDataRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken);

    Task<ReferenceDataRefreshResult> RefreshAsync(AccountType account, CancellationToken cancellationToken);

    /// Last refresh timestamp per account (UTC); null if never refreshed in this process.
    DateTime? LastRefreshUtc(AccountType account);

    IReadOnlyList<Symbol> Snapshot(AccountType account);
}
