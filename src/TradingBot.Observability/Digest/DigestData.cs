using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;

namespace TradingBot.Observability.Digest;

/// <summary>
/// Aggregated daily-digest payload built by <see cref="DailyDigestJob"/>
/// and rendered by <see cref="DigestRenderer"/>.
/// </summary>
public sealed record DigestData(
    DateTime DayStartUtc,
    DateTime DayEndUtc,
    IReadOnlyList<TradeHistory>    ClosedTrades,
    IReadOnlyList<Position>        OpenPositions,
    AccountSnapshot?               EquityStart,
    AccountSnapshot?               EquityEnd,
    IReadOnlyList<AlertJournalRow> AlertRows,
    decimal                        AiCostUsd);
