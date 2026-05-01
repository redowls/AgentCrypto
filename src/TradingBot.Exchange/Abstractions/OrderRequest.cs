namespace TradingBot.Exchange.Abstractions;

/// Exchange-agnostic order placement request. The gateway translates this
/// into the Spot or USDⓈ-M Futures wire format, after applying tick / step /
/// minNotional filters.
public sealed record OrderRequest
{
    public required AccountType Account     { get; init; }
    public required string      Symbol      { get; init; }
    public required string      ClientOrderId { get; init; }
    public required string      Side        { get; init; }     // BUY / SELL
    public required string      OrderType   { get; init; }     // LIMIT / MARKET / STOP_MARKET / TAKE_PROFIT_MARKET / LIMIT_MAKER
    public required decimal     Quantity    { get; init; }
    public decimal?             Price       { get; init; }
    public decimal?             StopPrice   { get; init; }
    public string?              TimeInForce { get; init; }     // GTC / IOC / FOK / GTX
    public bool                 ReduceOnly  { get; init; }
    public string?              PositionSide { get; init; }    // LONG / SHORT (futures hedge mode)
    public string                CorrelationId { get; init; } = string.Empty;
}
