using System.Collections.Concurrent;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Models;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// Buffer fed by the <c>POST /newsfeed/push</c> webhook endpoint. n8n (or any
/// other producer) ships items to the bot via HTTP; the analyzer drains the
/// queue on its next poll.
///
/// Thread-safe: producers add concurrently, the consumer drains under a
/// snapshot read so items added mid-drain reappear next cycle.
/// </summary>
public sealed class InMemoryWebhookNewsSource : INewsSource
{
    private readonly ConcurrentQueue<NewsItem> _queue = new();

    public string Name => "Webhook";

    public void Enqueue(NewsItem item) => _queue.Enqueue(item);

    public Task<IReadOnlyList<NewsItem>> FetchSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        if (_queue.IsEmpty) return Task.FromResult<IReadOnlyList<NewsItem>>(Array.Empty<NewsItem>());

        var bag = new List<NewsItem>(_queue.Count);
        while (_queue.TryDequeue(out var item))
        {
            // Items pushed via webhook may have stale timestamps if n8n
            // batches; we honor the watermark like the other sources.
            if (item.TimestampUtc > sinceUtc) bag.Add(item);
        }
        return Task.FromResult<IReadOnlyList<NewsItem>>(bag);
    }
}
