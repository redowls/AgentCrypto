namespace TradingBot.Exchange.Abstractions;

public sealed record OrderResult
{
    public required string  Symbol         { get; init; }
    public required string  ClientOrderId  { get; init; }
    public required long    ExchangeOrderId { get; init; }
    public required string  Status         { get; init; }
    public required decimal ExecutedQty    { get; init; }
    public decimal?         AvgFillPrice   { get; init; }
    public DateTime         TransactTimeUtc { get; init; }
}
