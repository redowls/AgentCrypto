using FluentAssertions;
using TradingBot.AI.Sentiment;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class WebhookPayloadTests
{
    [Fact]
    public void Single_item_payload_normalises_to_one_NewsItem()
    {
        var p = new WebhookNewsPayload
        {
            Source   = "n8n",
            Timestamp = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc),
            Headline = "BTC ETF approved",
        };
        var items = p.Normalize();
        items.Should().ContainSingle();
        items[0].Source.Should().Be("n8n");
        items[0].Headline.Should().Be("BTC ETF approved");
        items[0].TimestampUtc.Should().Be(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Items_array_normalises_to_one_NewsItem_per_entry()
    {
        var p = new WebhookNewsPayload
        {
            Source = "n8n",
            Items =
            [
                new WebhookNewsItem { Source = "X", Headline = "A", Timestamp = DateTime.UtcNow },
                new WebhookNewsItem { Headline = "B" },                  // Source falls back to envelope
                new WebhookNewsItem { Headline = "" },                   // Skipped (empty headline)
            ],
        };
        var items = p.Normalize();
        items.Should().HaveCount(2);
        items[0].Source.Should().Be("X");
        items[1].Source.Should().Be("n8n");   // inherited from envelope
    }

    [Fact]
    public void Empty_payload_produces_no_items()
    {
        new WebhookNewsPayload().Normalize().Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_source_dequeues_only_items_newer_than_watermark()
    {
        var src = new InMemoryWebhookNewsSource();
        var t0 = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        src.Enqueue(new(t0.AddMinutes(-5), "x", "stale"));
        src.Enqueue(new(t0.AddMinutes(+5), "x", "fresh"));

        var fetched = await src.FetchSinceAsync(t0, CancellationToken.None);
        fetched.Should().ContainSingle().Which.Headline.Should().Be("fresh");
    }
}
