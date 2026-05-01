namespace TradingBot.Core.Domain;

public sealed class AccountSnapshot
{
    public long     SnapshotId     { get; set; }
    public string   AccountType    { get; set; } = string.Empty;
    public DateTime SnapshotTime   { get; set; }
    public decimal  EquityUsd      { get; set; }
    public decimal  AvailableUsd   { get; set; }
    public decimal  UnrealizedPnl  { get; set; }
    public int      OpenPositions  { get; set; }
    public decimal  GrossExposure  { get; set; }
    public decimal  NetExposure    { get; set; }
    public decimal  Drawdown       { get; set; }
}
