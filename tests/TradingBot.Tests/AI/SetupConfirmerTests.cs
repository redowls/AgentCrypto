using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;
using TradingBot.AI.Setup;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class SetupConfirmerTests
{
    [Fact]
    public void Parse_extracts_approve_concerns_and_size_adj()
    {
        var ok = ClaudeSetupConfirmer.TryParse(
            "{\"approve\":true,\"confidence\":0.72,\"concerns\":[\"late entry\"],\"size_adj\":0.85}",
            out var v);
        ok.Should().BeTrue();
        v.Approve.Should().BeTrue();
        v.Confidence.Should().Be(0.72m);
        v.Concerns.Should().BeEquivalentTo(new[] { "late entry" });
        v.SizeAdj.Should().Be(0.85m);
        v.IsFallback.Should().BeFalse();
    }

    [Fact]
    public void Parse_clamps_size_adj_to_legal_range()
    {
        ClaudeSetupConfirmer.TryParse(
            "{\"approve\":false,\"confidence\":0.4,\"concerns\":[],\"size_adj\":2.0}", out var hi);
        hi.SizeAdj.Should().Be(1.0m);

        ClaudeSetupConfirmer.TryParse(
            "{\"approve\":false,\"confidence\":0.4,\"concerns\":[],\"size_adj\":0.1}", out var lo);
        lo.SizeAdj.Should().Be(0.5m);
    }

    [Fact]
    public async Task Timeout_returns_fallback_approve_with_configured_size_adj()
    {
        var fakeClaude = new Mock<IClaudeClient>();
        fakeClaude.Setup(c => c.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CacheControl>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CacheControl, string?, string?, int, CancellationToken>(
                async (_, _, _, _, _, _, _, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return new AiResponse("{}", 1, 1, 0, 0, 5000, 0.001m, false);
                });

        var opts = Options.Create(new SetupConfirmerOptions
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            FallbackSizeAdj = 0.7m,
        });
        var confirmer = new ClaudeSetupConfirmer(fakeClaude.Object, opts, NullLogger<ClaudeSetupConfirmer>.Instance);

        var verdict = await confirmer.ConfirmAsync(SampleContext(), CancellationToken.None);

        verdict.IsFallback.Should().BeTrue();
        verdict.Approve.Should().BeTrue();
        verdict.SizeAdj.Should().Be(0.7m);
    }

    [Fact]
    public async Task Budget_exceeded_returns_fallback_approve()
    {
        var fakeClaude = new Mock<IClaudeClient>();
        fakeClaude.Setup(c => c.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CacheControl>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException(0.01m, 0.05m));

        var opts = Options.Create(new SetupConfirmerOptions { Timeout = TimeSpan.FromSeconds(2) });
        var confirmer = new ClaudeSetupConfirmer(fakeClaude.Object, opts, NullLogger<ClaudeSetupConfirmer>.Instance);

        var verdict = await confirmer.ConfirmAsync(SampleContext(), CancellationToken.None);

        verdict.IsFallback.Should().BeTrue();
        verdict.Approve.Should().BeTrue();
    }

    [Fact]
    public async Task Successful_response_returns_parsed_verdict()
    {
        var json = JsonSerializer.Serialize(new
        {
            approve    = true,
            confidence = 0.8,
            concerns   = new[] { "late entry" },
            size_adj   = 0.9,
        });
        var fakeClaude = new Mock<IClaudeClient>();
        fakeClaude.Setup(c => c.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CacheControl>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse(json, 100, 50, 0, 0, 100, 0.001m, false));

        var opts = Options.Create(new SetupConfirmerOptions { Timeout = TimeSpan.FromSeconds(2) });
        var confirmer = new ClaudeSetupConfirmer(fakeClaude.Object, opts, NullLogger<ClaudeSetupConfirmer>.Instance);

        var verdict = await confirmer.ConfirmAsync(SampleContext(), CancellationToken.None);

        verdict.IsFallback.Should().BeFalse();
        verdict.Approve.Should().BeTrue();
        verdict.SizeAdj.Should().Be(0.9m);
    }

    private static SetupContext SampleContext() => new(
        Strategy: "BREAKOUT_DON",
        Symbol: "BTCUSDT",
        Side: "BUY",
        Entry: 60000m,
        StopLoss: 58000m,
        TakeProfit: 64000m,
        AtrMultipleStop: 1.5m,
        AtrMultipleTake: 3.0m,
        RuleRegime: "TRENDING_UP",
        RuleAdx: 28m,
        SentimentScore6h: 0.3m,
        SentimentItems6h: 2,
        BreakoutMagnitudePct: 0.4m,
        VolumeXSma20: 1.6m,
        Ema200DistancePct: 5.0m,
        StrategyHistorySummary: "2W/1L, avg R = +0.30",
        RuleConfidence: 0.6m);
}
