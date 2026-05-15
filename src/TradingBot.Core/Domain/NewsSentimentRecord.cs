namespace TradingBot.Core.Domain;

/// One row of <c>dbo.NewsSentiment</c> — one Claude verdict per (item, asset).
/// Multiple assets per item produce multiple rows sharing
/// <see cref="HeadlineHash"/>.
public sealed class NewsSentimentRecord
{
    public long     NewsSentimentId { get; set; }
    public DateTime ItemTimestamp   { get; set; }
    public string   Source          { get; set; } = string.Empty;
    public string   HeadlineHash    { get; set; } = string.Empty;
    public string   Headline        { get; set; } = string.Empty;
    public string   Asset           { get; set; } = string.Empty;
    public decimal  Sentiment       { get; set; }
    public decimal  Confidence      { get; set; }
    public string   Horizon         { get; set; } = string.Empty;
    public string?  Rationale       { get; set; }
    public bool     Actionable      { get; set; }
    public long?    AiInteractionId { get; set; }
    public DateTime CreatedAt       { get; set; }
}
