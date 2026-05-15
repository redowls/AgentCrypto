namespace TradingBot.AI.Abstractions;

/// String codes stored in <c>dbo.AiInteractions.Purpose</c>. The cache lookup
/// is keyed on (Purpose, Model, InputHash) so changing a purpose code makes
/// older rows invisible to that purpose's cache (intentional: changing the
/// prompt template should invalidate the cache).
public static class AiPurposes
{
    public const string Sentiment    = "SENTIMENT";
    public const string Regime       = "REGIME";
    public const string Confirmation = "CONFIRMATION";
    public const string Journal      = "JOURNAL";
}
