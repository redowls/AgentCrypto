namespace TradingBot.Core.Domain;

public sealed class ExecutionDiagnostic
{
    public long     DiagnosticId         { get; set; }
    public long     OrderId              { get; set; }
    public long?    FillId               { get; set; }
    public long?    SignalId             { get; set; }
    public int      SymbolId             { get; set; }
    public string   Side                 { get; set; } = string.Empty;
    public string   OrderType            { get; set; } = string.Empty;
    public decimal  ExpectedPrice        { get; set; }
    public decimal  ActualPrice          { get; set; }
    public decimal  Quantity             { get; set; }
    public decimal  ExpectedSlippageBps  { get; set; }
    public decimal  ObservedSlippageBps  { get; set; }
    public decimal? SpreadBps            { get; set; }
    public decimal? ParticipationPct     { get; set; }
    public string   ModelVersion         { get; set; } = "v1";
    public DateTime RecordedAt           { get; set; }
}

public sealed class BracketLink
{
    public long      BracketLinkId      { get; set; }
    public long      PositionId         { get; set; }
    public long      StopOrderId        { get; set; }
    public long      TakeProfitOrderId  { get; set; }
    public string    StopClientOrderId  { get; set; } = string.Empty;
    public string    TpClientOrderId    { get; set; } = string.Empty;
    public string    AccountType        { get; set; } = string.Empty;
    public int       SymbolId           { get; set; }
    public string    Status             { get; set; } = "ACTIVE";
    public string?   ReservedSibling    { get; set; }
    public DateTime  CreatedAt          { get; set; }
    public DateTime? ResolvedAt         { get; set; }
}
