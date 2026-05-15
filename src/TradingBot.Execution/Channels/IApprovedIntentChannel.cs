using System.Threading.Channels;
using TradingBot.Core.Domain;

namespace TradingBot.Execution.Channels;

/// <summary>
/// Single in-process channel into which the §7 risk gate pushes every
/// signal it just APPROVED, sized. The §8 <c>ExecutionEngine</c> is the
/// sole reader; on each event it generates the entry order and submits.
///
/// Bounded with <c>FullMode=Wait</c> so a stuck engine back-pressures the
/// risk approval stage, which back-pressures the strategy engine — same
/// chain as the kline / bar-close pipeline.
/// </summary>
public interface IApprovedIntentChannel
{
    ChannelReader<ApprovedIntent> Reader { get; }
    ChannelWriter<ApprovedIntent> Writer { get; }
    int CurrentCount { get; }
    int Capacity { get; }
}

/// <param name="Signal">Persisted signal row (Status=APPROVED in dbo.Signals).</param>
/// <param name="Quantity">Sized quantity from <see cref="TradingBot.Risk.Abstractions.RiskDecision.Quantity"/>.</param>
/// <param name="RiskUsd">Risk dollars at sizing time (audit only).</param>
/// <param name="NotionalUsd">Notional at sizing time (audit only).</param>
/// <param name="AccountType">"SPOT" or "UMFUT" — pinned by the risk gate.</param>
/// <param name="SymbolCode">Ticker (e.g. BTCUSDT) — denormalised so the
/// engine doesn't need a second DB hop.</param>
public sealed record ApprovedIntent(
    Signal  Signal,
    decimal Quantity,
    decimal RiskUsd,
    decimal NotionalUsd,
    string  AccountType,
    string  SymbolCode);
