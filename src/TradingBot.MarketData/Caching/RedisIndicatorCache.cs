using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TradingBot.Core.Indicators;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.Caching;

/// <summary>
/// Latest indicator snapshot per (symbol, interval) under
/// <c>{prefix}{symbol}:{tf}</c>. One Redis hash per key keeps individual
/// indicator reads cheap (HGET over a full payload deserialise) while still
/// being one round trip on write (HSET multi-field).
/// </summary>
public sealed class RedisIndicatorCache : IIndicatorCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;

    public RedisIndicatorCache(IConnectionMultiplexer redis, IOptions<MarketDataOptions> options)
    {
        _redis = redis;
        _prefix = options.Value.IndicatorKeyPrefix;
    }

    public async Task SetAsync(string symbol, string interval, IndicatorSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        var key = Key(symbol, interval);

        var fields = new HashEntry[]
        {
            new("asOf",            ToIso(snapshot.AsOfUtc)),
            new("atr14",           ToOpt(snapshot.Atr14)),
            new("ema9",            ToOpt(snapshot.Ema9)),
            new("ema21",           ToOpt(snapshot.Ema21)),
            new("ema50",           ToOpt(snapshot.Ema50)),
            new("ema200",          ToOpt(snapshot.Ema200)),
            new("adx14",           ToOpt(snapshot.Adx14)),
            new("plusDi14",        ToOpt(snapshot.PlusDi14)),
            new("minusDi14",       ToOpt(snapshot.MinusDi14)),
            new("rsi14",           ToOpt(snapshot.Rsi14)),
            new("bbUpper",         ToOpt(snapshot.BbUpper)),
            new("bbMid",           ToOpt(snapshot.BbMid)),
            new("bbLower",         ToOpt(snapshot.BbLower)),
            new("bbWidth",         ToOpt(snapshot.BbWidth)),
            new("donchianUpper",   ToOpt(snapshot.DonchianUpper)),
            new("donchianLower",   ToOpt(snapshot.DonchianLower)),
            new("vwapSession",     ToOpt(snapshot.VwapSession)),
            new("atr50Sma",        ToOpt(snapshot.Atr50Sma)),
            new("bbWidthSma50",    ToOpt(snapshot.BbWidthSma50)),
            new("bbWidthPctRank",  ToOpt(snapshot.BbWidthPercentileRank)),
            new("bbWidthPrev",     ToOpt(snapshot.BbWidthPrev)),
            new("adxPrev",         ToOpt(snapshot.AdxPrev)),
        };

        await db.HashSetAsync(key, fields).ConfigureAwait(false);
    }

    public async Task<IndicatorSnapshot?> TryGetAsync(string symbol, string interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _redis.GetDatabase().HashGetAllAsync(Key(symbol, interval)).ConfigureAwait(false);
        if (entries.Length == 0) return null;

        var m = entries.ToDictionary(e => e.Name.ToString(), e => e.Value, StringComparer.Ordinal);

        return new IndicatorSnapshot(
            FromIso(m["asOf"]),
            FromOpt(m, "atr14"),
            FromOpt(m, "ema9"),
            FromOpt(m, "ema21"),
            FromOpt(m, "ema50"),
            FromOpt(m, "ema200"),
            FromOpt(m, "adx14"),
            FromOpt(m, "plusDi14"),
            FromOpt(m, "minusDi14"),
            FromOpt(m, "rsi14"),
            FromOpt(m, "bbUpper"),
            FromOpt(m, "bbMid"),
            FromOpt(m, "bbLower"),
            FromOpt(m, "bbWidth"),
            FromOpt(m, "donchianUpper"),
            FromOpt(m, "donchianLower"),
            FromOpt(m, "vwapSession"),
            FromOpt(m, "atr50Sma"),
            FromOpt(m, "bbWidthSma50"),
            FromOpt(m, "bbWidthPctRank"),
            FromOpt(m, "bbWidthPrev"),
            FromOpt(m, "adxPrev"));
    }

    private string Key(string symbol, string interval) =>
        $"{_prefix}{symbol.ToUpperInvariant()}:{interval}";

    private static RedisValue ToOpt(decimal? v) =>
        v is null ? RedisValue.Null : v.Value.ToString("G29", CultureInfo.InvariantCulture);

    private static decimal? FromOpt(IReadOnlyDictionary<string, RedisValue> m, string field)
    {
        if (!m.TryGetValue(field, out var v) || v.IsNull) return null;
        var s = v.ToString();
        if (string.IsNullOrEmpty(s)) return null;
        return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string ToIso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime FromIso(RedisValue v) =>
        DateTime.Parse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
