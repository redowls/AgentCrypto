namespace TradingBot.Risk.Abstractions;

/// String codes for <see cref="RiskDecision.RejectReason"/>. These are the
/// authoritative values written to <c>dbo.Signals.Reason</c> and
/// <c>dbo.RiskEvents.EventType</c>; the §8 audit log replays from them.
public static class RiskRejectReasons
{
    /// §8.2 — daily realized+unrealized P&amp;L &lt; -3% since 00:00 UTC.
    public const string DailyLossLimit          = "DAILY_LOSS_LIMIT";
    /// §8.2 — drawdown from HWM ≤ -15%; full halt of new entries.
    public const string MaxDrawdownHalt         = "MAX_DRAWDOWN_HALT";
    /// §8.2 — already at or above the 4-position concurrency cap.
    public const string MaxConcurrentPositions  = "MAX_CONCURRENT_POSITIONS";
    /// §8.3 — same correlation cluster already has an open position on the same side.
    public const string CorrelationClusterOccupied = "CORRELATION_CLUSTER_OCCUPIED";
    /// §8.4 — drawdown ladder produced a 0× multiplier (defensive: only fires
    /// if the §8.2 -15% gate ever yields, i.e. equity is in &gt;-15% territory).
    public const string DerislHalt              = "DERISK_HALT";
    /// §8.2 — gross exposure would exceed 200% of equity (futures cap).
    public const string GrossExposure           = "GROSS_EXPOSURE";
    /// §8.2 — single-symbol cap clamp produced a quantity below the exchange
    /// minimum notional or step size; the trade can't be expressed under caps.
    public const string SingleSymbolCapInfeasible = "SINGLE_SYMBOL_CAP_INFEASIBLE";
    /// Lot/notional clamp produced a zero/sub-minimum quantity.
    public const string LotFilterInfeasible     = "LOT_FILTER_INFEASIBLE";
    /// §8.2 — funding rate is hostile and we'd be on the paying side.
    public const string FundingRateHostile      = "FUNDING_RATE_HOSTILE";
    /// Global kill-switch is tripped (HTTP 418, manual halt, recon drift, etc.).
    public const string KillSwitchActive        = "KILL_SWITCH_ACTIVE";
    /// Configuration / data prerequisites missing — symbol unknown, snapshot
    /// stale, etc. Treated as a soft reject; the caller should retry.
    public const string PreconditionFailed      = "PRECONDITION_FAILED";
}
