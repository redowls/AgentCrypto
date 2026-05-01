namespace TradingBot.Exchange.Abstractions;

public sealed record KlineData(
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    decimal  Open,
    decimal  High,
    decimal  Low,
    decimal  Close,
    decimal  Volume,
    decimal  QuoteVolume,
    int      TradeCount,
    decimal  TakerBuyBase,
    bool     IsClosed);
