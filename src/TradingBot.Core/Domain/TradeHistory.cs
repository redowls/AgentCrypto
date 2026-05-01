namespace TradingBot.Core.Domain;

public sealed class TradeHistory
{
    public long     TradeHistoryId { get; set; }
    public long     PositionId     { get; set; }
    public int      SymbolId       { get; set; }
    public string   Strategy       { get; set; } = string.Empty;
    public string   Side           { get; set; } = string.Empty;
    public DateTime EntryTime      { get; set; }
    public DateTime ExitTime       { get; set; }
    public int      HoldingMinutes { get; set; }
    public decimal  EntryPrice     { get; set; }
    public decimal  ExitPrice      { get; set; }
    public decimal  Quantity       { get; set; }
    public decimal  GrossPnlUsd    { get; set; }
    public decimal  FeesUsd        { get; set; }
    public decimal  NetPnlUsd      { get; set; }
    public decimal  RMultiple      { get; set; }   // SQL column: R_Multiple
    public string   ExitReason     { get; set; } = string.Empty;
}
