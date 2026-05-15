using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface IExecutionDiagnosticsRepository
{
    /// Idempotent insert keyed on (OrderId, FillId). Duplicate fill events
    /// resulting in the same row are silently dropped.
    Task<long> InsertAsync(ExecutionDiagnostic row, CancellationToken cancellationToken);
}

public interface IBracketLinkRepository
{
    Task<long> InsertAsync(BracketLink link, CancellationToken cancellationToken);

    Task<BracketLink?> GetActiveByPositionAsync(long positionId, CancellationToken cancellationToken);

    /// Look up the bracket link that owns the given clientOrderId on either leg.
    Task<BracketLink?> GetByLegClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    /// Atomically reserves the sibling cancellation slot. Returns true when
    /// the caller wins the reservation and SHOULD proceed to issue the
    /// cancel; false when another worker beat us. <paramref name="leg"/>
    /// names the leg the *winner* fills (so the sibling is the OTHER leg).
    Task<bool> TryReserveSiblingCancelAsync(long bracketLinkId, string leg, CancellationToken cancellationToken);

    Task<int> MarkResolvedAsync(long bracketLinkId, DateTime resolvedAtUtc, CancellationToken cancellationToken);
}
