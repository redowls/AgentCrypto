namespace TradingBot.Core.Domain;

public sealed class RiskEvent
{
    public long     RiskEventId { get; set; }
    public DateTime EventTime   { get; set; }
    public string   EventType   { get; set; } = string.Empty;
    public string   Severity    { get; set; } = string.Empty;
    public int?     SymbolId    { get; set; }
    public long?    SignalId    { get; set; }
    public long?    OrderId     { get; set; }
    public string?  Payload     { get; set; }   // JSON
    public bool     Acted       { get; set; }
}
