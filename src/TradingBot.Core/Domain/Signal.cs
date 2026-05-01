namespace TradingBot.Core.Domain;

public sealed class Signal
{
    public long     SignalId       { get; set; }
    public int      SymbolId       { get; set; }
    public string   Strategy       { get; set; } = string.Empty;
    public string   Interval       { get; set; } = string.Empty;
    public DateTime BarOpenTime    { get; set; }
    public string   Side           { get; set; } = string.Empty;
    public decimal  EntryPrice     { get; set; }
    public decimal  StopLoss       { get; set; }
    public decimal  TakeProfit     { get; set; }
    public decimal? AtrValue       { get; set; }
    public string?  Regime         { get; set; }
    public decimal? SentimentScore { get; set; }
    public decimal? AiConfidence   { get; set; }
    public decimal  Confidence     { get; set; }
    public string   Status         { get; set; } = string.Empty;
    public string?  Reason         { get; set; }
    public DateTime CreatedAt      { get; set; }
}
