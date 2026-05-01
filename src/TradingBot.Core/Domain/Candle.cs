namespace TradingBot.Core.Domain;

public sealed class Candle
{
    public int      SymbolId     { get; set; }
    public string   Interval     { get; set; } = string.Empty;
    public DateTime OpenTime     { get; set; }
    public DateTime CloseTime    { get; set; }
    public decimal  Open         { get; set; }
    public decimal  High         { get; set; }
    public decimal  Low          { get; set; }
    public decimal  Close        { get; set; }
    public decimal  Volume       { get; set; }
    public decimal  QuoteVolume  { get; set; }
    public int      TradeCount   { get; set; }
    public decimal  TakerBuyBase { get; set; }
    public bool     IsClosed     { get; set; }
    public DateTime InsertedAt   { get; set; }
}
