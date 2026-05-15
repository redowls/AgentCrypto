using Microsoft.Extensions.Logging;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.Account;

/// <summary>
/// §8 real-time account view. On every <see cref="GetCurrentAsync"/>:
///   1. Pull the cash balance from the Binance gateway for the account.
///   2. Walk every open <c>dbo.Positions</c> row, fold it against the live
///      mark price → unrealized PnL + gross / net exposure.
///   3. Read the all-time HWM from <c>dbo.AccountSnapshots</c>.
///   4. Anchor "since 00:00 UTC" against the first snapshot at or after that
///      instant; falls back to current equity when the day's first snapshot
///      hasn't been written yet (DailyPnlPct = 0 in that case).
///
/// Persistence is delegated to <see cref="AccountSnapshotPersister"/>; this
/// provider is read-only and side-effect free.
/// </summary>
public sealed class AccountSnapshotProvider : IAccountSnapshotProvider
{
    // USD-equivalents counted as cash. Non-stable balances are ignored at this
    // layer because their value already shows up via positions (PnL on open
    // crypto is captured in the MTM step).
    private static readonly HashSet<string> StableQuoteAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "BUSD", "USDC", "FDUSD", "TUSD",
    };

    private readonly IPositionRepository _positions;
    private readonly IAccountSnapshotRepository _snapshots;
    private readonly ISymbolRepository _symbols;
    private readonly IBinanceGatewayResolver _gateways;
    private readonly IMarkPriceProvider _marks;
    private readonly IClock _clock;
    private readonly ILogger<AccountSnapshotProvider> _log;

    public AccountSnapshotProvider(
        IPositionRepository positions,
        IAccountSnapshotRepository snapshots,
        ISymbolRepository symbols,
        IBinanceGatewayResolver gateways,
        IMarkPriceProvider marks,
        IClock clock,
        ILogger<AccountSnapshotProvider> log)
    {
        _positions = positions;
        _snapshots = snapshots;
        _symbols = symbols;
        _gateways = gateways;
        _marks = marks;
        _clock = clock;
        _log = log;
    }

    public async Task<AccountRiskState> GetCurrentAsync(string accountType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountType);

        var account = ResolveAccount(accountType);
        var nowUtc = _clock.UtcNow;

        // 1. Cash balance.
        decimal cashUsd;
        try
        {
            var info = await _gateways.Get(account).GetAccountAsync(cancellationToken).ConfigureAwait(false);
            cashUsd = info.Balances
                .Where(b => StableQuoteAssets.Contains(b.Asset))
                .Sum(b => b.Free + b.Locked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Account snapshot: failed to fetch cash balance for {Account}; falling back to last snapshot.",
                accountType);
            var prev = await _snapshots.GetLatestAsync(accountType, cancellationToken).ConfigureAwait(false);
            cashUsd = prev?.AvailableUsd ?? 0m;
        }

        // 2. Open positions → MTM.
        var openAll = await _positions.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        var open = openAll
            .Where(p => string.Equals(p.AccountType, accountType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        decimal unrealized = 0m;
        decimal gross = 0m;
        decimal net = 0m;
        foreach (var pos in open)
        {
            var symRow = await _symbols.GetByIdAsync(pos.SymbolId, cancellationToken).ConfigureAwait(false);
            decimal markPrice;
            if (symRow is null)
            {
                markPrice = pos.AvgEntryPrice;
            }
            else
            {
                var mp = await _marks
                    .TryGetMarkPriceAsync(accountType, symRow.SymbolCode, cancellationToken)
                    .ConfigureAwait(false);
                markPrice = mp ?? pos.AvgEntryPrice;
            }

            var sign = string.Equals(pos.Side, PositionSides.Long, StringComparison.OrdinalIgnoreCase) ? +1m : -1m;
            var pnl = sign * (markPrice - pos.AvgEntryPrice) * pos.Quantity;
            unrealized += pnl;

            var notional = pos.Quantity * markPrice;
            gross += Math.Abs(notional);
            net   += sign * notional;
        }

        var equity = cashUsd + unrealized;

        // 3. HWM.
        var hwmFromDb = await _snapshots.GetMaxEquityAsync(accountType, cancellationToken).ConfigureAwait(false) ?? 0m;
        var hwm = Math.Max(hwmFromDb, equity);
        var dd = hwm > 0m ? (equity / hwm) - 1m : 0m;
        if (dd > 0m) dd = 0m;

        // 4. Daily anchor.
        var startOfDayUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var anchor = await _snapshots
            .GetFirstAtOrAfterAsync(accountType, startOfDayUtc, cancellationToken)
            .ConfigureAwait(false);

        var equityAt00 = anchor?.EquityUsd ?? equity;
        var dailyPct = equityAt00 > 0m ? (equity / equityAt00) - 1m : 0m;

        return new AccountRiskState(
            AccountType:      accountType,
            EquityUsd:        equity,
            AvailableUsd:     cashUsd,
            UnrealizedPnlUsd: unrealized,
            OpenPositions:    open.Count,
            GrossExposureUsd: gross,
            NetExposureUsd:   net,
            HighWaterMarkUsd: hwm,
            DrawdownPct:      dd,
            DailyPnlPct:      dailyPct,
            EquityAt00UtcUsd: equityAt00,
            SnapshotTimeUtc:  nowUtc);
    }

    private static AccountType ResolveAccount(string code) =>
        string.Equals(code, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? AccountType.UmFutures
            : AccountType.Spot;
}
