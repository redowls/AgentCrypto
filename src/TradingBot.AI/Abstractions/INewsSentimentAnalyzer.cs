using TradingBot.AI.Models;

namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.4.1 — accepts a batch of news items, calls Claude with the cached
/// sentiment system prompt, parses the NDJSON response into one verdict per
/// (item, asset) pair, and persists each verdict to <c>dbo.NewsSentiment</c>.
///
/// Returns the parsed verdicts so callers (e.g. signal-engine enrichment)
/// can use them immediately without a DB round-trip.
/// </summary>
public interface INewsSentimentAnalyzer
{
    Task<IReadOnlyList<NdjsonSentiment>> AnalyzeAsync(
        IReadOnlyList<NewsItem> items,
        CancellationToken       cancellationToken);
}
