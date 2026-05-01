using TradingBot.Core.Domain;
using TradingBot.MarketData.Abstractions;

namespace TradingBot.MarketData.Ingestion;

internal static class CandleMapper
{
    public static Candle ToCandle(KlineEvent evt) => new()
    {
        SymbolId     = evt.SymbolId,
        Interval     = evt.Interval,
        OpenTime     = DateTime.SpecifyKind(evt.Kline.OpenTimeUtc, DateTimeKind.Utc),
        CloseTime    = DateTime.SpecifyKind(evt.Kline.CloseTimeUtc, DateTimeKind.Utc),
        Open         = evt.Kline.Open,
        High         = evt.Kline.High,
        Low          = evt.Kline.Low,
        Close        = evt.Kline.Close,
        Volume       = evt.Kline.Volume,
        QuoteVolume  = evt.Kline.QuoteVolume,
        TradeCount   = evt.Kline.TradeCount,
        TakerBuyBase = evt.Kline.TakerBuyBase,
        IsClosed     = evt.Kline.IsClosed,
    };
}
