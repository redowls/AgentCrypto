using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TradingBot.Exchange.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.Caching;

/// <summary>
/// Redis-backed live (in-progress) candle store. Each (symbol, interval) maps
/// to a single hash; fields encode the latest unclosed bar. We use a hash
/// rather than a serialized blob so partial readers (e.g. dashboards) can
/// query individual fields without fetching/parsing the whole record.
/// </summary>
public sealed class RedisLiveCandleCache : ILiveCandleCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;

    public RedisLiveCandleCache(IConnectionMultiplexer redis, IOptions<MarketDataOptions> options)
    {
        _redis = redis;
        _prefix = options.Value.LiveCandleKeyPrefix;
    }

    public async Task SetAsync(KlineEvent evt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        var key = Key(evt.Symbol, evt.Interval);

        var fields = new HashEntry[]
        {
            new("symbol",       evt.Symbol),
            new("interval",     evt.Interval),
            new("account",      evt.Account.ToString()),
            new("openTime",     ToIso(evt.Kline.OpenTimeUtc)),
            new("closeTime",    ToIso(evt.Kline.CloseTimeUtc)),
            new("open",         ToInv(evt.Kline.Open)),
            new("high",         ToInv(evt.Kline.High)),
            new("low",          ToInv(evt.Kline.Low)),
            new("close",        ToInv(evt.Kline.Close)),
            new("volume",       ToInv(evt.Kline.Volume)),
            new("quoteVolume",  ToInv(evt.Kline.QuoteVolume)),
            new("tradeCount",   evt.Kline.TradeCount),
            new("takerBuyBase", ToInv(evt.Kline.TakerBuyBase)),
            new("isClosed",     evt.Kline.IsClosed ? 1 : 0),
        };

        await db.HashSetAsync(key, fields).ConfigureAwait(false);
    }

    public async Task<KlineEvent?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        var entries = await db.HashGetAllAsync(Key(symbol, interval)).ConfigureAwait(false);
        if (entries.Length == 0) return null;

        var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value, StringComparer.Ordinal);

        var account = Enum.TryParse<AccountType>(map.GetValueOrDefault("account").ToString(), out var a)
            ? a : AccountType.Spot;

        var k = new KlineData(
            FromIso(map["openTime"]!),
            FromIso(map["closeTime"]!),
            FromInv(map["open"]!),
            FromInv(map["high"]!),
            FromInv(map["low"]!),
            FromInv(map["close"]!),
            FromInv(map["volume"]!),
            FromInv(map["quoteVolume"]!),
            (int)map["tradeCount"],
            FromInv(map["takerBuyBase"]!),
            (int)map["isClosed"] == 1);

        // SymbolId isn't stored in Redis (read-only consumers don't need it).
        return new KlineEvent(0, symbol, interval, account, k, KlineSource.WebSocket);
    }

    public async Task RemoveAsync(string symbol, string interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _redis.GetDatabase().KeyDeleteAsync(Key(symbol, interval)).ConfigureAwait(false);
    }

    private string Key(string symbol, string interval) =>
        $"{_prefix}{symbol.ToUpperInvariant()}:{interval}";

    private static string ToInv(decimal d) => d.ToString("G29", CultureInfo.InvariantCulture);
    private static decimal FromInv(RedisValue v) => decimal.Parse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string ToIso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime FromIso(RedisValue v) => DateTime.Parse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
