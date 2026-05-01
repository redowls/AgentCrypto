using System.Threading.Channels;
using TradingBot.Core.Domain;

namespace TradingBot.Strategies.Channels;

/// <summary>
/// Single in-process channel into which the S6 SignalEngine publishes every
/// signal it just persisted (Status=GENERATED). Downstream consumers (the S7
/// AI confirmer / S8 risk gate) read from <see cref="Reader"/> and proceed to
/// transition the signal to APPROVED/REJECTED.
///
/// Keeping the fan-out as a channel instead of a direct method call decouples
/// the strategy hot path from the AI/risk pipeline's latency: the SignalEngine
/// can finish persisting and move on to the next bar even if Claude is slow.
/// </summary>
public interface IGeneratedSignalChannel
{
    ChannelReader<GeneratedSignalEvent> Reader { get; }
    ChannelWriter<GeneratedSignalEvent> Writer { get; }
    int CurrentCount { get; }
    int Capacity { get; }
}

/// <summary>
/// Notification carrying enough context for the next stage to act without
/// hitting the database. The persisted <see cref="Signal"/> row carries
/// <see cref="Signal.SignalId"/>, so the AI/risk stages can update its status
/// without re-querying.
/// </summary>
/// <param name="Signal">The persisted GENERATED signal (SignalId populated).</param>
/// <param name="SizeMultiplier">§3.4 strategy-selector size multiplier (e.g. 0.5×
/// for VOLATILE/BREAKOUT). Carried alongside the signal so the risk module
/// doesn't have to re-derive it from the regime + strategy.</param>
public sealed record GeneratedSignalEvent(Signal Signal, decimal SizeMultiplier);
