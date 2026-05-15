namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Outcome of <see cref="IRiskManager.ApproveAsync"/>. The risk gate is the
/// only entry-side gate in front of the execution engine; every signal lands
/// here as <see cref="Approved"/> with a sized quantity or as
/// <see cref="Rejected"/> with a reason code that maps 1:1 to the §8 gates.
/// </summary>
public sealed class RiskDecision
{
    private RiskDecision(
        bool approved,
        decimal quantity,
        string? rejectReason,
        string? message,
        decimal? riskUsd,
        decimal? notionalUsd,
        decimal? kFactor,
        decimal? volAdjust)
    {
        Approved     = approved;
        Quantity     = quantity;
        RejectReason = rejectReason;
        Message      = message;
        RiskUsd      = riskUsd;
        NotionalUsd  = notionalUsd;
        KFactor      = kFactor;
        VolAdjust    = volAdjust;
    }

    public bool     Approved      { get; }
    public decimal  Quantity      { get; }
    public string?  RejectReason  { get; }
    public string?  Message       { get; }
    /// Risk dollars used for sizing (only when approved). For audit / journals.
    public decimal? RiskUsd       { get; }
    /// Final notional after lot/notional clamping + caps.
    public decimal? NotionalUsd   { get; }
    /// §8.4 ladder factor that produced the size.
    public decimal? KFactor       { get; }
    /// §8.1 volatility adjustment that produced the size.
    public decimal? VolAdjust     { get; }

    public static RiskDecision Approve(
        decimal quantity,
        decimal riskUsd,
        decimal notionalUsd,
        decimal kFactor,
        decimal volAdjust,
        string? message = null) =>
        new(true, quantity, null, message, riskUsd, notionalUsd, kFactor, volAdjust);

    public static RiskDecision Reject(string reason, string? message = null) =>
        new(false, 0m, reason, message, null, null, null, null);

    public override string ToString() =>
        Approved
            ? $"APPROVE qty={Quantity} risk=${RiskUsd:F2} notional=${NotionalUsd:F2} k={KFactor} volAdj={VolAdjust}"
            : $"REJECT  reason={RejectReason} ({Message})";
}
