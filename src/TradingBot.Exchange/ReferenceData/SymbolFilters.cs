using System.Collections.Concurrent;
using TradingBot.Core.Domain;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.ReferenceData;

/// In-memory snapshot of exchangeInfo, refreshed by ReferenceDataService.
/// Reads are lock-free; the snapshot is replaced atomically per account.
public sealed class SymbolFilters : ISymbolFilters
{
    private readonly ConcurrentDictionary<AccountType, IReadOnlyDictionary<string, Symbol>> _byAccount = new();
    private readonly ConcurrentDictionary<AccountType, IReadOnlyList<Symbol>> _allByAccount = new();

    public Symbol? TryGet(AccountType account, string symbolCode) =>
        _byAccount.TryGetValue(account, out var map) && map.TryGetValue(symbolCode.ToUpperInvariant(), out var sym)
            ? sym
            : null;

    public Symbol Get(AccountType account, string symbolCode) =>
        TryGet(account, symbolCode)
            ?? throw new InvalidOperationException(
                $"Symbol '{symbolCode}' not loaded for account {account}. Has the reference data service run?");

    public IReadOnlyList<Symbol> All(AccountType account) =>
        _allByAccount.TryGetValue(account, out var list) ? list : Array.Empty<Symbol>();

    internal void Replace(AccountType account, IReadOnlyList<Symbol> rows)
    {
        var map = rows.ToDictionary(r => r.SymbolCode, StringComparer.OrdinalIgnoreCase);
        _byAccount[account] = map;
        _allByAccount[account] = rows;
    }
}
