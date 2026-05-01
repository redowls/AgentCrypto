using System.Collections.Concurrent;
using TradingBot.MarketData.Abstractions;

namespace TradingBot.MarketData.Caching;

public sealed class InMemoryLiveCandleCache : ILiveCandleCache
{
    private readonly ConcurrentDictionary<string, KlineEvent> _byKey = new(StringComparer.Ordinal);

    public Task SetAsync(KlineEvent evt, CancellationToken cancellationToken)
    {
        _byKey[Key(evt.Symbol, evt.Interval)] = evt;
        return Task.CompletedTask;
    }

    public Task<KlineEvent?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.TryGetValue(Key(symbol, interval), out var v) ? v : null);

    public Task RemoveAsync(string symbol, string interval, CancellationToken cancellationToken)
    {
        _byKey.TryRemove(Key(symbol, interval), out _);
        return Task.CompletedTask;
    }

    private static string Key(string symbol, string interval) =>
        $"{symbol.ToUpperInvariant()}:{interval}";
}
