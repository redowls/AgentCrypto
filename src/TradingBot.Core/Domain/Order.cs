namespace TradingBot.Core.Domain;

public sealed class Order
{
    public long     OrderId         { get; set; }
    public long?    SignalId        { get; set; }
    public int      SymbolId        { get; set; }
    public string   AccountType     { get; set; } = string.Empty;
    public string   ClientOrderId   { get; set; } = string.Empty;
    public long?    ExchangeOrderId { get; set; }
    public string   OrderType       { get; set; } = string.Empty;
    public string   Side            { get; set; } = string.Empty;
    public string?  PositionSide    { get; set; }
    public decimal  Quantity        { get; set; }
    public decimal? Price           { get; set; }
    public decimal? StopPrice       { get; set; }
    public string?  TimeInForce     { get; set; }
    public bool     ReduceOnly      { get; set; }
    public string   Status          { get; set; } = string.Empty;
    public decimal  FilledQty       { get; set; }
    public decimal? AvgFillPrice    { get; set; }
    public decimal  CommissionPaid  { get; set; }
    public string?  CommissionAsset { get; set; }
    public DateTime SubmittedAt     { get; set; }
    public DateTime LastUpdatedAt   { get; set; }
    public string?  Notes           { get; set; }
}
