namespace TradingBot.AI.Models;

/// One news headline ready for sentiment analysis. Multiple <see cref="NewsItem"/>s
/// are batched into a single Claude call (per §5.4.1) — the classifier returns
/// one NDJSON line per (item, asset) pair.
public sealed record NewsItem(
    DateTime TimestampUtc,
    string   Source,
    string   Headline);
