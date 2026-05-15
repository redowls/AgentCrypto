using FluentAssertions;
using TradingBot.AI.Sentiment;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class NewsSentimentAnalyzerTests
{
    [Fact]
    public void Parse_ndjson_returns_one_verdict_per_line()
    {
        var ndjson =
            "{\"asset\":\"BTC\",\"sentiment\":0.8,\"confidence\":0.9,\"horizon\":\"INTRADAY\",\"rationale\":\"strong\",\"actionable\":true}\n" +
            "{\"asset\":\"ETH\",\"sentiment\":-0.3,\"confidence\":0.6,\"horizon\":\"SWING\",\"rationale\":\"weak\",\"actionable\":false}\n";

        var verdicts = NewsSentimentAnalyzer.ParseNdjson(ndjson);

        verdicts.Should().HaveCount(2);
        verdicts[0].Asset.Should().Be("BTC");
        verdicts[0].Sentiment.Should().Be(0.8m);
        verdicts[0].Actionable.Should().BeTrue();
        verdicts[1].Asset.Should().Be("ETH");
        verdicts[1].Sentiment.Should().Be(-0.3m);
        verdicts[1].Horizon.Should().Be("SWING");
    }

    [Fact]
    public void Parse_ndjson_skips_blank_and_malformed_lines()
    {
        var ndjson =
            "\n" +
            "{not json}\n" +
            "{\"asset\":\"BTC\",\"sentiment\":0.5,\"confidence\":0.5,\"horizon\":\"INTRADAY\",\"rationale\":\"\",\"actionable\":false}\n" +
            "```\n";

        var verdicts = NewsSentimentAnalyzer.ParseNdjson(ndjson);
        verdicts.Should().ContainSingle()
                .Which.Asset.Should().Be("BTC");
    }

    [Fact]
    public void Parse_ndjson_clamps_sentiment_and_confidence_to_legal_range()
    {
        var ndjson =
            "{\"asset\":\"BTC\",\"sentiment\":1.5,\"confidence\":1.4,\"horizon\":\"INTRADAY\",\"rationale\":\"\",\"actionable\":true}\n" +
            "{\"asset\":\"ETH\",\"sentiment\":-2.0,\"confidence\":-0.1,\"horizon\":\"SWING\",\"rationale\":\"\",\"actionable\":false}\n";

        var verdicts = NewsSentimentAnalyzer.ParseNdjson(ndjson);
        verdicts[0].Sentiment.Should().Be(1m);
        verdicts[0].Confidence.Should().Be(1m);
        verdicts[1].Sentiment.Should().Be(-1m);
        verdicts[1].Confidence.Should().Be(0m);
    }

    [Fact]
    public void Parse_ndjson_uppercases_asset_and_horizon()
    {
        var ndjson =
            "{\"asset\":\"btc\",\"sentiment\":0.1,\"confidence\":0.5,\"horizon\":\"intraday\",\"rationale\":\"\",\"actionable\":true}\n";
        var verdicts = NewsSentimentAnalyzer.ParseNdjson(ndjson);
        verdicts[0].Asset.Should().Be("BTC");
        verdicts[0].Horizon.Should().Be("INTRADAY");
    }
}
