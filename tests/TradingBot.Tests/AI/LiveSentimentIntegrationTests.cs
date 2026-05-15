using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Claude;
using TradingBot.AI.Configuration;
using TradingBot.AI.Cost;
using TradingBot.AI.Models;
using TradingBot.AI.Prompts;
using TradingBot.AI.Sentiment;
using Xunit;

namespace TradingBot.Tests.AI;

/// <summary>
/// xUnit fact attribute that auto-skips when ANTHROPIC_API_KEY is not set.
/// Mirrors the BinanceTestnetFact pattern used elsewhere in the suite —
/// keeps live tests off CI by default.
/// </summary>
public sealed class AnthropicLiveFactAttribute : FactAttribute
{
    public AnthropicLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            Skip = "ANTHROPIC_API_KEY env var not set; skipping live AI integration test.";
        }
    }
}

[Trait("Category", "Live")]
public sealed class LiveSentimentIntegrationTests
{
    [AnthropicLiveFact]
    public async Task Real_sentiment_call_returns_schema_valid_json()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!;

        var clock = new FakeClock(DateTime.UtcNow);
        var opt = Options.Create(new ClaudeOptions
        {
            Model            = "claude-sonnet-4-5",
            DailyCapUsd      = 0.50m,
            RequestsPerMinute = 60,
            RequestTimeoutMs  = 30_000,
        });

        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        ClaudeClient.ConfigureHttpClient(httpClient, opt.Value, key);

        IHttpClientFactory factory = new SingletonHttpClientFactory(httpClient);
        var cache   = new InMemoryAiResponseCache(clock);
        var meter   = new DailyCostMeter(opt, clock);
        var limiter = new TokenBucketRateLimiter(opt, clock);
        var claude  = new ClaudeClient(factory, opt, cache, meter, limiter,
            new TradingBot.Core.Observability.NullTradingMetrics(),
            NullLogger<ClaudeClient>.Instance);

        var items = new List<NewsItem>
        {
            new(DateTime.UtcNow,
                "TestFeed",
                "BREAKING: Bitcoin surges past $80,000 as institutional inflows hit record highs"),
        };

        var resp = await claude.SendAsync(
            purpose:        AiPurposes.Sentiment,
            systemPrompt:   SystemPrompts.Sentiment,
            userPrompt:     UserPromptRenderer.SentimentBatch(items),
            cache:          CacheControl.Sentiment,
            cancellationToken: CancellationToken.None);

        resp.Json.Should().NotBeNullOrWhiteSpace();
        resp.InputTokens.Should().BeGreaterThan(0);
        resp.OutputTokens.Should().BeGreaterThan(0);

        var verdicts = NewsSentimentAnalyzer.ParseNdjson(resp.Json);
        verdicts.Should().NotBeEmpty("Claude must produce at least one schema-valid NDJSON line");

        var first = verdicts[0];
        first.Sentiment.Should().BeInRange(-1m, 1m);
        first.Confidence.Should().BeInRange(0m, 1m);
        new[] { "INTRADAY", "SWING", "LONG" }.Should().Contain(first.Horizon);
    }

    private sealed class SingletonHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
