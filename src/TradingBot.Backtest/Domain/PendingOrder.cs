using TradingBot.Core.Domain.Enums;

namespace TradingBot.Backtest.Domain;

// In-memory order tracked by SimulatedExchange. After submission an order
// sits here until a bar's range crosses its trigger condition, at which
// point it generates a SimulatedFill and transitions to terminal Filled.
internal sealed class PendingOrder
{
    public long      LocalOrderId    { get; init; }            // bt.Orders.OrderId (assigned at insert)
    public long      ExchangeOrderId { get; init; }            // simulated monotonic
    public required string ClientOrderId { get; init; }
    public required string Symbol      { get; init; }
    public required string Side        { get; init; }          // BUY / SELL
    public required string OrderType   { get; init; }          // MARKET / LIMIT / STOP_MARKET / TAKE_PROFIT_MARKET
    public required decimal Quantity   { get; init; }
    public decimal? Price              { get; init; }          // LIMIT only
    public decimal? StopPrice          { get; init; }          // STOP_MARKET / TAKE_PROFIT_MARKET only
    public string?  TimeInForce        { get; init; }
    public bool     ReduceOnly         { get; init; }
    public string?  PositionSide       { get; init; }
    public required string CorrelationId { get; init; }
    public DateTime  SubmittedAt       { get; init; }
    public string    Status            { get; set; } = OrderStatuses.New;
    public bool      EligibleFromOpenOfNextBar { get; init; } = true;  // never fills on the bar of submission
}

// Concrete fill produced by the simulated exchange. Mirrors the fields the
// live UserDataReactor would receive and persist to dbo.Fills.
internal sealed record SimulatedFill(
    long     LocalOrderId,
    long     ExchangeOrderId,
    string   ClientOrderId,
    string   Symbol,
    string   Side,
    string   OrderType,
    decimal  Quantity,
    decimal  Price,
    decimal  CommissionUsd,
    string   CommissionAsset,
    bool     IsMaker,
    DateTime FillTimeUtc,
    string   ExitReason);   // empty when this is an entry fill

internal static class FillExitReasons
{
    public const string Entry      = "";
    public const string TakeProfit = "TP";
    public const string StopLoss   = "SL";
    public const string Trailing   = "TRAIL";
    public const string Time       = "TIME";
    public const string Manual     = "MANUAL";
}
