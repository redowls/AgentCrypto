using TradingBot.Core.Domain;

namespace TradingBot.Risk.Abstractions;

/// <summary>
/// §8.5 risk gate. Sits between the strategy <c>SignalEngine</c> and the
/// execution engine: every <see cref="Signal"/> with <c>Status=GENERATED</c>
/// flows through <see cref="ApproveAsync"/>, which either returns a sized
/// quantity (<see cref="RiskDecision.Approve"/>) or rejects with one of the
/// codes in <see cref="RiskRejectReasons"/>.
///
/// The implementation is purely synchronous logic over the inputs supplied —
/// callers are expected to obtain the <see cref="AccountRiskState"/> from
/// <see cref="IAccountSnapshotProvider"/> first.
/// </summary>
public interface IRiskManager
{
    Task<RiskDecision> ApproveAsync(
        Signal signal,
        AccountRiskState account,
        CancellationToken cancellationToken);
}
