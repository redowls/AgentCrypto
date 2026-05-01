namespace TradingBot.Core.Domain;

/// <summary>
/// Persisted regime classification (one row in <c>dbo.Regimes</c>). Note this
/// is the *DB shape* — it stores the regime as the string code so the column
/// is human-readable in queries; consumers translate to the
/// <c>TradingBot.Core.Indicators.Regime</c> enum at use sites.
/// </summary>
public sealed class RegimeRecord
{
    public long     RegimeId   { get; set; }
    public int      SymbolId   { get; set; }
    public string   Interval   { get; set; } = string.Empty;
    public DateTime AsOf       { get; set; }
    public string   Regime     { get; set; } = string.Empty;
    public decimal  Confidence { get; set; }
    public string   Source     { get; set; } = "RULE";
    public string?  Inputs     { get; set; }
    public DateTime CreatedAt  { get; set; }
}
