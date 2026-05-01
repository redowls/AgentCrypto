namespace TradingBot.Core.Domain;

// SQL column `Symbol` is exposed as the C# property `SymbolCode` (C# disallows
// a member with the same name as its enclosing type). The repository SELECTs
// alias the column accordingly.
public sealed class Symbol
{
    public int      SymbolId    { get; set; }
    public string   Exchange    { get; set; } = string.Empty;
    public string   SymbolCode  { get; set; } = string.Empty;   // SQL column: Symbol (ticker, e.g. BTCUSDT)
    public string   BaseAsset   { get; set; } = string.Empty;
    public string   QuoteAsset  { get; set; } = string.Empty;
    public decimal  TickSize    { get; set; }
    public decimal  StepSize    { get; set; }
    public decimal  MinNotional { get; set; }
    public bool     IsActive    { get; set; } = true;
    public DateTime UpdatedAt   { get; set; }
}
