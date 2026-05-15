namespace TradingBot.Core.Domain;

/// One row of <c>dbo.AiJournals</c> — the markdown report Claude produced for
/// an ISO week. The on-disk file is a convenience copy; the DB row is the
/// source of truth.
public sealed class AiJournalRecord
{
    public long     AiJournalId     { get; set; }
    public int      IsoYear         { get; set; }
    public int      IsoWeek         { get; set; }
    public DateTime PeriodStartUtc  { get; set; }
    public DateTime PeriodEndUtc    { get; set; }
    public int      TradesAnalyzed  { get; set; }
    public string   Markdown        { get; set; } = string.Empty;
    public long?    AiInteractionId { get; set; }
    public DateTime CreatedAt       { get; set; }
}
