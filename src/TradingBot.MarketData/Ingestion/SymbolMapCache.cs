using System.Collections.Concurrent;
using TradingBot.Data.Abstractions;

namespace TradingBot.MarketData.Ingestion;

/// <summary>
/// In-memory (Symbol, Account) → SymbolId lookup. Resolved lazily against the
/// Symbols table, then cached for the lifetime of the process. The Symbols
/// table is updated daily by the reference-data service; the lookup result
/// won't change for an active symbol.
///
/// Thread-safe. Used by the ingestor's WS callback path where we can't afford
/// a database round-trip per kline.
/// </summary>
public sealed class SymbolMapCache
{
    private readonly ConcurrentDictionary<(string Exchange, string Symbol), int> _byKey =
        new(KeyComparer.Instance);

    public bool TryGet(string exchange, string symbol, out int symbolId) =>
        _byKey.TryGetValue((exchange, symbol), out symbolId);

    public void Set(string exchange, string symbol, int symbolId) =>
        _byKey[(exchange, symbol)] = symbolId;

    public async Task<int> ResolveAsync(
        ISymbolRepository repo,
        string exchange,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (TryGet(exchange, symbol, out var existing)) return existing;

        var row = await repo.GetByExchangeAndCodeAsync(exchange, symbol, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Symbol '{symbol}' not present in dbo.Symbols for exchange '{exchange}'. " +
                "Run the reference-data refresh service before starting market-data ingestion.");

        Set(exchange, symbol, row.SymbolId);
        return row.SymbolId;
    }

    private sealed class KeyComparer : IEqualityComparer<(string Exchange, string Symbol)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((string Exchange, string Symbol) x, (string Exchange, string Symbol) y) =>
            string.Equals(x.Exchange, y.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Exchange, string Symbol) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Exchange),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Symbol));
    }
}
