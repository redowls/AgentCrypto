using FluentAssertions;
using TradingBot.AI.Prompts;
using Xunit;

namespace TradingBot.Tests.AI;

/// <summary>
/// §5.4 prompts must remain verbatim. Any edit is a behavioural change and
/// must be a deliberate test update — not a silent prompt drift.
/// </summary>
public sealed class SystemPromptsTests
{
    [Fact]
    public void Sentiment_prompt_matches_design_doc_5_4_1()
    {
        const string expected =
            "You are a crypto news sentiment classifier for an algorithmic trading bot.\n" +
            "You read short crypto news headlines and produce a structured JSON verdict.\n" +
            "Sentiment scale: -1.0 (very bearish) ... +1.0 (very bullish), 0 = neutral.\n" +
            "Confidence scale: 0.0 ... 1.0.\n" +
            "You MUST output ONLY a JSON object. No prose, no markdown.\n\n" +
            "Schema:\n" +
            "{\n" +
            "  \"asset\": \"<ticker or GLOBAL>\",\n" +
            "  \"sentiment\": <number -1..+1>,\n" +
            "  \"confidence\": <number 0..1>,\n" +
            "  \"horizon\": \"INTRADAY|SWING|LONG\",\n" +
            "  \"rationale\": \"<<= 25 words>\",\n" +
            "  \"actionable\": true|false\n" +
            "}";
        SystemPrompts.Sentiment.Should().Be(expected);
        SystemPrompts.SentimentNdjsonFooter.Should().Be(
            "For each item, return one JSON object on its own line (NDJSON).");
    }

    [Fact]
    public void Regime_prompt_matches_design_doc_5_4_2()
    {
        const string expected =
            "You are a market regime classifier. Given current technical readings, classify the\n" +
            "regime as exactly one of: TRENDING_UP, TRENDING_DOWN, RANGING, VOLATILE, COMPRESSING.\n" +
            "Output strict JSON with regime, confidence (0..1), and a 1-line reason.";
        SystemPrompts.Regime.Should().Be(expected);
    }

    [Fact]
    public void Setup_confirmer_prompt_matches_design_doc_5_4_3()
    {
        const string expected =
            "You are a senior trade reviewer for a quant crypto bot.\n" +
            "Given a proposed trade and supporting context, output a JSON verdict:\n" +
            "{ \"approve\": true|false, \"confidence\": 0..1, \"concerns\": [\"...\"], \"size_adj\": 0.5..1.0 }\n" +
            "Reject only on clear red flags: major news contradiction, regime mismatch, late-cycle entry.";
        SystemPrompts.SetupConfirmer.Should().Be(expected);
    }

    [Fact]
    public void Journal_prompt_matches_design_doc_5_4_4()
    {
        const string expected =
            "You are an objective post-trade analyst. Given a CSV of last week's closed trades with\n" +
            "context, produce: (a) top 3 patterns of winners, (b) top 3 patterns of losers,\n" +
            "(c) one concrete parameter or rule change to test next week (with hypothesis).\n" +
            "Markdown output.";
        SystemPrompts.Journal.Should().Be(expected);
    }
}
