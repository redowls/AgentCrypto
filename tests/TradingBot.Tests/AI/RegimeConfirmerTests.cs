using FluentAssertions;
using TradingBot.AI.Regime;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class RegimeConfirmerTests
{
    [Fact]
    public void Parse_extracts_regime_confidence_and_reason()
    {
        var ok = ClaudeRegimeConfirmer.TryParseRegime(
            "{\"regime\":\"TRENDING_UP\",\"confidence\":0.83,\"reason\":\"ADX rising, EMAs aligned\"}",
            out var r, out var c, out var reason);
        ok.Should().BeTrue();
        r.Should().Be(Regime.TrendingUp);
        c.Should().Be(0.83m);
        reason.Should().Be("ADX rising, EMAs aligned");
    }

    [Fact]
    public void Parse_strips_markdown_fences()
    {
        var ok = ClaudeRegimeConfirmer.TryParseRegime(
            "```json\n{\"regime\":\"RANGING\",\"confidence\":0.5,\"reason\":\"flat\"}\n```",
            out var r, out var c, out _);
        ok.Should().BeTrue();
        r.Should().Be(Regime.Ranging);
        c.Should().Be(0.5m);
    }

    [Fact]
    public void Parse_returns_false_when_unknown_or_malformed()
    {
        ClaudeRegimeConfirmer.TryParseRegime("not json", out _, out _, out _).Should().BeFalse();
        ClaudeRegimeConfirmer.TryParseRegime("{\"regime\":\"BOGUS\"}", out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_clamps_confidence_to_zero_one()
    {
        ClaudeRegimeConfirmer.TryParseRegime(
            "{\"regime\":\"VOLATILE\",\"confidence\":1.7,\"reason\":\"x\"}", out _, out var c, out _);
        c.Should().Be(1m);
    }
}
