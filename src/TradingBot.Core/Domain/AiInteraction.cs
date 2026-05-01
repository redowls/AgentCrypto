namespace TradingBot.Core.Domain;

public sealed class AiInteraction
{
    public long     AiInteractionId { get; set; }
    public string   Purpose         { get; set; } = string.Empty;
    public string   Model           { get; set; } = string.Empty;
    public string   InputHash       { get; set; } = string.Empty;
    public string   InputJson       { get; set; } = string.Empty;
    public string?  OutputJson      { get; set; }
    public int?     InputTokens     { get; set; }
    public int?     OutputTokens    { get; set; }
    public int?     LatencyMs       { get; set; }
    public decimal? CostUsd         { get; set; }
    public DateTime CreatedAt       { get; set; }
}
