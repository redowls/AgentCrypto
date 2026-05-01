using TradingBot.MarketData.Abstractions;

namespace TradingBot.MarketData.Caching;

/// <summary>
/// Stores the latest in-progress (unclosed) kline per (symbol, interval).
/// Strategies that want intra-bar reactivity read from here; the canonical
/// dbo.Candles table only ever contains <c>IsClosed = 1</c> rows.
///
/// Implementations:
/// - <see cref="RedisLiveCandleCache"/> when MarketData:RedisConnectionString
///   is set.
/// - <see cref="InMemoryLiveCandleCache"/> otherwise (process-local).
/// </summary>
public interface ILiveCandleCache
{
    Task SetAsync(KlineEvent evt, CancellationToken cancellationToken);
    Task<KlineEvent?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken);
    Task RemoveAsync(string symbol, string interval, CancellationToken cancellationToken);
}
