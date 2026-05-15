namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.4.4 — Sunday 06:00 UTC weekly post-trade analyst. Pulls the last 7 days
/// of <c>dbo.TradeHistory</c> + linked Signals, formats them as CSV, calls
/// Claude through the Message Batches API for the 50% discount, persists the
/// markdown to <c>dbo.AiJournals</c> + <c>/journals/YYYY-WW.md</c>, and
/// returns the file path so the n8n digest workflow can email it.
/// </summary>
public interface IPostTradeJournalist
{
    Task<JournalRunResult> GenerateWeeklyJournalAsync(
        DateTime          weekEndUtc,
        CancellationToken cancellationToken);
}

/// Output of one weekly journal run. <see cref="MarkdownPath"/> is null when
/// the run was skipped (no trades to analyze).
public sealed record JournalRunResult(
    int       IsoYear,
    int       IsoWeek,
    int       TradesAnalyzed,
    string?   MarkdownPath,
    long?     AiInteractionId,
    bool      Skipped,
    string?   SkipReason);
