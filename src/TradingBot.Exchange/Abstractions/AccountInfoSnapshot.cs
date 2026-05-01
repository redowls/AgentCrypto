namespace TradingBot.Exchange.Abstractions;

public sealed record AccountBalance(string Asset, decimal Free, decimal Locked);

public sealed record AccountInfoSnapshot(
    AccountType Account,
    bool        CanTrade,
    bool        CanWithdraw,
    bool        CanDeposit,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyList<AccountBalance> Balances);
