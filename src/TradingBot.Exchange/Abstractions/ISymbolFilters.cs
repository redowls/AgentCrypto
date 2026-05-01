using TradingBot.Core.Domain;

namespace TradingBot.Exchange.Abstractions;

/// Read-only view of the latest filter snapshot for a symbol/account, surfaced
/// to the execution engine. Backed by ReferenceDataService.
public interface ISymbolFilters
{
    /// Returns the cached symbol filter or null if the symbol is unknown.
    Symbol? TryGet(AccountType account, string symbolCode);

    /// Throws InvalidOperationException if the symbol is not loaded.
    Symbol Get(AccountType account, string symbolCode);

    /// Snapshot of all loaded symbols for the account.
    IReadOnlyList<Symbol> All(AccountType account);
}
