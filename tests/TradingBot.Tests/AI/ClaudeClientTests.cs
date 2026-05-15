using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Claude;
using TradingBot.AI.Configuration;
using TradingBot.AI.Cost;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class ClaudeClientTests
{
    private static (ClaudeClient client, StubHttpMessageHandler handler, InMemoryAiResponseCache cache,
                    DailyCostMeter meter, FakeClock clock)
        Build(decimal capUsd = 100m, int rpm = 60)
    {
        var clock   = new FakeClock(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        var handler = new StubHttpMessageHandler();
        var http    = new StubHttpClientFactory(handler, "https://api.anthropic.com");
        var opt = Options.Create(new ClaudeOptions
        {
            ApiKeySecretName = "Anthropic:ApiKey",
            Model            = "claude-sonnet-4-5",
            DailyCapUsd      = capUsd,
            RequestsPerMinute = rpm,
            RequestTimeoutMs = 5000,
        });
        var cache   = new InMemoryAiResponseCache(clock);
        var meter   = new DailyCostMeter(opt, clock);
        var limiter = new TokenBucketRateLimiter(opt, clock);
        var client  = new ClaudeClient(http, opt, cache, meter, limiter,
            new TradingBot.Core.Observability.NullTradingMetrics(),
            NullLogger<ClaudeClient>.Instance);
        return (client, handler, cache, meter, clock);
    }

    private static string AnthropicOk(string text, int inTokens = 100, int outTokens = 50)
        => JsonSerializer.Serialize(new
        {
            id = "msg_test",
            model = "claude-sonnet-4-5",
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = inTokens, output_tokens = outTokens, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
        });

    [Fact]
    public async Task Sends_system_prompt_with_cache_control_when_requested()
    {
        var (client, handler, _, _, _) = Build();
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("ok")));

        await client.SendAsync(
            purpose: AiPurposes.Sentiment,
            systemPrompt: "SYSTEM-PROMPT",
            userPrompt: "USER-PROMPT",
            cache: CacheControl.Sentiment,
            cancellationToken: CancellationToken.None);

        handler.RequestBodies.Should().ContainSingle();
        var body = handler.RequestBodies[0];
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be("claude-sonnet-4-5");
        root.GetProperty("system")[0].GetProperty("text").GetString().Should().Be("SYSTEM-PROMPT");
        // cache_control marker pinned to "ephemeral" per Anthropic prompt-caching docs.
        root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString()
            .Should().Be("ephemeral");
        root.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("USER-PROMPT");
    }

    [Fact]
    public async Task Omits_cache_control_when_caller_disables_anthropic_cache()
    {
        var (client, handler, _, _, _) = Build();
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("ok")));

        await client.SendAsync(
            purpose: AiPurposes.Journal,
            systemPrompt: "S", userPrompt: "U",
            cache: CacheControl.None,
            cancellationToken: CancellationToken.None);

        var body = handler.RequestBodies[0];
        body.Should().NotContain("cache_control");
    }

    [Fact]
    public async Task Local_cache_hit_short_circuits_second_call()
    {
        var (client, handler, cache, _, _) = Build();
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("first")));

        var r1 = await client.SendAsync(AiPurposes.Sentiment, "S", "U", CacheControl.Sentiment);
        var r2 = await client.SendAsync(AiPurposes.Sentiment, "S", "U", CacheControl.Sentiment);

        r1.FromCache.Should().BeFalse();
        r2.FromCache.Should().BeTrue();
        r2.Json.Should().Be("first");
        handler.CallCount.Should().Be(1, "the second call must hit the local cache");
        cache.Store.Should().ContainSingle();
    }

    [Fact]
    public async Task Local_cache_miss_after_ttl_expires()
    {
        var (client, handler, _, _, clock) = Build();
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("v")));

        await client.SendAsync(AiPurposes.Confirmation, "S", "U", CacheControl.Confirmation);
        clock.Advance(TimeSpan.FromMinutes(1)); // > 30s TTL
        await client.SendAsync(AiPurposes.Confirmation, "S", "U", CacheControl.Confirmation);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Daily_cap_throws_AiBudgetExceededException()
    {
        var (client, handler, _, meter, _) = Build(capUsd: 0.01m);
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("v", inTokens: 10_000, outTokens: 1_000)));

        // First call uses ~$0.045 → blows the $0.01 cap once recorded.
        var r1 = await client.SendAsync(AiPurposes.Sentiment, "S", "U1", CacheControl.None);
        r1.CostUsd.Should().BeGreaterThan(0.01m);
        meter.SpentTodayUsd.Should().BeGreaterThan(meter.DailyCapUsd);

        var act = async () => await client.SendAsync(AiPurposes.Sentiment, "S", "U2", CacheControl.None);
        await act.Should().ThrowAsync<AiBudgetExceededException>();
    }

    [Fact]
    public async Task Sends_x_api_key_header_on_every_request()
    {
        var clock   = new FakeClock(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        var handler = new StubHttpMessageHandler();
        var opt = Options.Create(new ClaudeOptions { DailyCapUsd = 100m, RequestsPerMinute = 60 });
        var http = new StubHttpClientFactory(handler, "https://api.anthropic.com");
        var cache = new InMemoryAiResponseCache(clock);

        // Build the HttpClient via the production helper so the auth header flow matches the real wiring.
        var realClient = http.CreateClient(ClaudeClient.HttpClientName);
        ClaudeClient.ConfigureHttpClient(realClient, opt.Value, "test-key-123");

        // Inject a custom factory that hands out the pre-configured client.
        var factory = new SingletonHttpClientFactory(realClient);
        var client = new ClaudeClient(factory, opt, cache, new DailyCostMeter(opt, clock),
            new TokenBucketRateLimiter(opt, clock),
            new TradingBot.Core.Observability.NullTradingMetrics(),
            NullLogger<ClaudeClient>.Instance);
        handler.Responder = _ => Task.FromResult(StubHttpMessageHandler.JsonOk(AnthropicOk("ok")));

        await client.SendAsync(AiPurposes.Sentiment, "S", "U", CacheControl.None);

        handler.Requests[0].Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("test-key-123");
        handler.Requests[0].Headers.GetValues("anthropic-version").Should().ContainSingle();
    }

    private sealed class SingletonHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
