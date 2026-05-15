namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Global atomic halt flag — extends the existing
/// <c>TradingBot.Exchange.Abstractions.IBinanceKillSwitch</c> (which captures
/// HTTP 418 specifically) with broader trip reasons drawn from §8.5 / §6:
/// daily loss limit, max-DD halt, manual command, reconciliation drift &gt; $5.
///
/// Tripped state is replicated to Redis so a restart picks it up; the DB row
/// in <c>dbo.RiskEvents</c> is the canonical audit trail. While tripped:
/// • <see cref="IRiskManager"/> rejects every new entry with
///   <see cref="RiskRejectReasons.KillSwitchActive"/>.
/// • The execution engine still allows reduceOnly exits to drain risk.
/// • A CRITICAL alert is raised through the alerting sink wired in S11.
/// </summary>
public interface IKillSwitch
{
    bool IsTripped { get; }
    string?   Reason       { get; }
    DateTime? TrippedAtUtc { get; }
    KillSwitchSource Source { get; }

    /// Idempotent — second trip with the same reason is a no-op (logs DEBUG).
    /// A different reason produced after the first trip overwrites the
    /// <see cref="Reason"/>/<see cref="Source"/> for visibility.
    Task TripAsync(KillSwitchSource source, string reason, CancellationToken cancellationToken);

    /// Operator action — clears the flag in Redis + writes an INFO RiskEvent.
    /// Does not auto-clear on its own; stays tripped until explicitly reset.
    Task ResetAsync(string operatorNote, CancellationToken cancellationToken);

    /// Synchronous read of the latest cached state. Implementations refresh
    /// from Redis lazily; callers in the hot path (RiskManager.ApproveAsync)
    /// rely on this returning instantly.
    void RefreshFromCache();
}

public enum KillSwitchSource
{
    None              = 0,
    /// HTTP 418 — IP banned. Wired to the existing exchange-level switch.
    Http418Ban        = 1,
    DailyLossLimit    = 2,
    MaxDrawdownHalt   = 3,
    ManualCommand     = 4,
    ReconciliationDrift = 5,
    /// Catch-all for forced trips from runbook scripts.
    OperatorAction    = 6,
}
