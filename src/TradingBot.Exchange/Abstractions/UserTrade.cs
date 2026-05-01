namespace TradingBot.Exchange.Abstractions;

public sealed record UserTrade(
    long     TradeId,
    long     OrderId,
    string   Symbol,
    string   Side,
    decimal  Price,
    decimal  Quantity,
    decimal  QuoteQuantity,
    decimal  Commission,
    string   CommissionAsset,
    bool     IsMaker,
    DateTime TimeUtc);
