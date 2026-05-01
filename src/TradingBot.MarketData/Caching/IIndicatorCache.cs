using TradingBot.Core.Indicators;

namespace TradingBot.MarketData.Caching;

/// <summary>
/// Latest computed indicator values per (symbol, interval). Stored in Redis
/// under <c>{prefix}{symbol}:{tf}</c> as a hash, or in process memory when
/// Redis isn't configured.
///
/// We pre-cache only the "latest" snapshot — strategies that need historical
/// indicator values recompute from candles on demand, which is cheap because
/// Skender computes a series in one pass.
/// </summary>
public interface IIndicatorCache
{
    Task SetAsync(string symbol, string interval, IndicatorSnapshot snapshot, CancellationToken cancellationToken);

    Task<IndicatorSnapshot?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken);
}
