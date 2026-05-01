namespace TradingBot.Core.Domain;

public sealed class Position
{
    public long      PositionId     { get; set; }
    public int       SymbolId       { get; set; }
    public string    AccountType    { get; set; } = string.Empty;
    public string    Side           { get; set; } = string.Empty;
    public long?     EntrySignalId  { get; set; }
    public long?     EntryOrderId   { get; set; }
    public decimal   Quantity       { get; set; }
    public decimal   AvgEntryPrice  { get; set; }
    public decimal   StopLoss       { get; set; }
    public decimal   TakeProfit     { get; set; }
    public decimal   InitialRiskUsd { get; set; }
    public DateTime  OpenedAt       { get; set; }
    public DateTime? ClosedAt       { get; set; }
    public decimal?  ClosePrice     { get; set; }
    public decimal?  RealizedPnlUsd { get; set; }
    public string    Status         { get; set; } = string.Empty;
}
