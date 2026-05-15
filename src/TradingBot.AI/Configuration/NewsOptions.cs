using System.ComponentModel.DataAnnotations;

namespace TradingBot.AI.Configuration;

public sealed class NewsOptions
{
    public const string SectionName = "News";

    /// <summary>Master switch. When false, neither the ingestion service nor
    /// the webhook are wired — useful for purely-rule deployments.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the ingestion BackgroundService polls the
    /// configured <c>INewsSource</c> chain. Default 5 min matches §5.4.1.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap on items per Claude call — sentiment input batches
    /// larger than this risk hitting the 200K-token context window.</summary>
    [Range(1, 200)]
    public int BatchSize { get; init; } = 25;

    /// <summary>CryptoPanic source — disabled by default (requires API
    /// key in secrets at <see cref="CryptoPanicTokenSecret"/>).</summary>
    public bool EnableCryptoPanic { get; init; } = false;

    public string CryptoPanicTokenSecret { get; init; } = "CryptoPanic:ApiKey";
    public string CryptoPanicEndpoint    { get; init; } = "https://cryptopanic.com/api/v1/posts/";

    /// <summary>RSS feeds polled as a fallback when CryptoPanic is disabled
    /// or rate-limited. Empty list = RSS source disabled.</summary>
    public string[] RssFeedUrls { get; init; } = [];

    /// <summary>Webhook (POST /newsfeed/push) — when true the Worker maps the
    /// endpoint that lets n8n ship items into the analyzer.</summary>
    public bool EnableWebhook { get; init; } = true;

    /// <summary>Optional shared secret. When set, the webhook checks for a
    /// matching <c>X-Webhook-Secret</c> header and rejects mismatches with 401.
    /// </summary>
    public string? WebhookSharedSecret { get; init; }
}
