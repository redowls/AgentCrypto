using TradingBot.AI.Models;

namespace TradingBot.AI.Abstractions;

/// <summary>
/// Pluggable news provider. The default deployment chains a CryptoPanic
/// source with an RSS-fallback source; an n8n webhook source pushes into
/// the same buffer through the <c>POST /newsfeed/push</c> endpoint.
///
/// Implementations should return only items strictly after the
/// <paramref name="sinceUtc"/> watermark so the ingestion service can run
/// incrementally without re-classifying old headlines.
/// </summary>
public interface INewsSource
{
    string Name { get; }

    Task<IReadOnlyList<NewsItem>> FetchSinceAsync(
        DateTime          sinceUtc,
        CancellationToken cancellationToken);
}
