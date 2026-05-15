namespace TradingBot.Core.Domain;

public sealed class CorrelationPair
{
    public long     CorrelationId { get; set; }
    public DateTime AsOf          { get; set; }
    public int      SymbolIdA     { get; set; }
    public int      SymbolIdB     { get; set; }
    public int      LookbackDays  { get; set; }
    public decimal  Correlation   { get; set; }
    public int      SampleCount   { get; set; }
    public DateTime CreatedAt     { get; set; }
}
