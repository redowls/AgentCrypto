namespace TradingBot.Core.Domain;

public sealed class Fill
{
    public long     FillId          { get; set; }
    public long     OrderId         { get; set; }
    public long     TradeId         { get; set; }
    public decimal  Quantity        { get; set; }
    public decimal  Price           { get; set; }
    public decimal  Commission      { get; set; }
    public string   CommissionAsset { get; set; } = string.Empty;
    public bool     IsMaker         { get; set; }
    public DateTime TradeTime       { get; set; }
}
