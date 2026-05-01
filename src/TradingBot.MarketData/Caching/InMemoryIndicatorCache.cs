using System.Collections.Concurrent;
using TradingBot.Core.Indicators;

namespace TradingBot.MarketData.Caching;

public sealed class InMemoryIndicatorCache : IIndicatorCache
{
    private readonly ConcurrentDictionary<string, IndicatorSnapshot> _byKey =
        new(StringComparer.Ordinal);

    public Task SetAsync(string symbol, string interval, IndicatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        _byKey[Key(symbol, interval)] = snapshot;
        return Task.CompletedTask;
    }

    public Task<IndicatorSnapshot?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.TryGetValue(Key(symbol, interval), out var v) ? v : null);

    private static string Key(string symbol, string interval) =>
        $"{symbol.ToUpperInvariant()}:{interval}";
}
