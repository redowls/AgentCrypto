using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.Account;

/// Writes a single <see cref="AccountRiskState"/> into <c>dbo.AccountSnapshots</c>.
/// Decoupled from the read path so unit tests can inspect sizing math without
/// hitting a database; the §9 Quartz schedule wires this on a 1-minute cadence.
public sealed class AccountSnapshotPersister : IAccountSnapshotPersister
{
    private readonly IAccountSnapshotRepository _repo;

    public AccountSnapshotPersister(IAccountSnapshotRepository repo) => _repo = repo;

    public Task<long> PersistAsync(AccountRiskState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = new AccountSnapshot
        {
            AccountType   = state.AccountType,
            SnapshotTime  = state.SnapshotTimeUtc,
            EquityUsd     = state.EquityUsd,
            AvailableUsd  = state.AvailableUsd,
            UnrealizedPnl = state.UnrealizedPnlUsd,
            OpenPositions = state.OpenPositions,
            GrossExposure = state.GrossExposureUsd,
            NetExposure   = state.NetExposureUsd,
            // Drawdown is stored with the column scale DECIMAL(7,4) — clamp.
            Drawdown      = ClampToScale(state.DrawdownPct),
        };
        return _repo.InsertAsync(row, cancellationToken);
    }

    private static decimal ClampToScale(decimal value)
    {
        // dbo.AccountSnapshots.Drawdown is DECIMAL(7,4): max ±999.9999. The
        // gate clips DD to ≤ 0 already; this is a defensive guard.
        if (value < -9.9999m) return -9.9999m;
        if (value >  9.9999m) return  9.9999m;
        return decimal.Round(value, 4, MidpointRounding.AwayFromZero);
    }
}
