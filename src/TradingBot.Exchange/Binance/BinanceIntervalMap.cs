using Binance.Net.Enums;

namespace TradingBot.Exchange.Binance;

internal static class BinanceIntervalMap
{
    public static KlineInterval Parse(string interval) => interval switch
    {
        "1m"  => KlineInterval.OneMinute,
        "3m"  => KlineInterval.ThreeMinutes,
        "5m"  => KlineInterval.FiveMinutes,
        "15m" => KlineInterval.FifteenMinutes,
        "30m" => KlineInterval.ThirtyMinutes,
        "1h"  => KlineInterval.OneHour,
        "2h"  => KlineInterval.TwoHour,
        "4h"  => KlineInterval.FourHour,
        "6h"  => KlineInterval.SixHour,
        "8h"  => KlineInterval.EightHour,
        "12h" => KlineInterval.TwelveHour,
        "1d"  => KlineInterval.OneDay,
        "3d"  => KlineInterval.ThreeDay,
        "1w"  => KlineInterval.OneWeek,
        "1M"  => KlineInterval.OneMonth,
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unknown kline interval.")
    };

    public static OrderSide ParseSide(string side) => side.ToUpperInvariant() switch
    {
        "BUY"  => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };

    public static TimeInForce? ParseTif(string? tif) => tif?.ToUpperInvariant() switch
    {
        null or "" => null,
        "GTC" => TimeInForce.GoodTillCanceled,
        "IOC" => TimeInForce.ImmediateOrCancel,
        "FOK" => TimeInForce.FillOrKill,
        "GTX" => TimeInForce.GoodTillCrossing,
        _ => throw new ArgumentOutOfRangeException(nameof(tif), tif, null),
    };

    public static PositionSide? ParsePositionSide(string? side) => side?.ToUpperInvariant() switch
    {
        null or "" or "BOTH" => null,
        "LONG"  => PositionSide.Long,
        "SHORT" => PositionSide.Short,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };
}
