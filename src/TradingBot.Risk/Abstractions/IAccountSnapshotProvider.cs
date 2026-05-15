namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Builds a real-time <see cref="AccountRiskState"/> on demand: fold open
/// <c>dbo.Positions</c> against current Binance mark prices, look up the HWM
/// from <c>dbo.AccountSnapshots</c>, and anchor the daily-loss-limit math
/// against the first snapshot at or after 00:00 UTC.
///
/// The risk gate calls this once per evaluated signal. A scheduled
/// <see cref="IAccountSnapshotPersister"/> writes the same view to
/// <c>dbo.AccountSnapshots</c> every minute (§9 schedule) so the HWM and
/// daily anchors are always populated.
/// </summary>
public interface IAccountSnapshotProvider
{
    Task<AccountRiskState> GetCurrentAsync(string accountType, CancellationToken cancellationToken);
}

/// <summary>
/// Periodic writer for <c>dbo.AccountSnapshots</c>. Decoupled from the read
/// path so a unit test can validate sizing math without touching a database.
/// </summary>
public interface IAccountSnapshotPersister
{
    Task<long> PersistAsync(AccountRiskState state, CancellationToken cancellationToken);
}
