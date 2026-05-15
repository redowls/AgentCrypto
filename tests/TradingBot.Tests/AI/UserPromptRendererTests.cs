using FluentAssertions;
using TradingBot.AI.Models;
using TradingBot.AI.Prompts;
using TradingBot.Core.Indicators;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class UserPromptRendererTests
{
    [Fact]
    public void Sentiment_user_block_wraps_each_item_in_xml_with_iso_ts()
    {
        var items = new List<NewsItem>
        {
            new(new DateTime(2026, 4, 25, 8, 12, 0, DateTimeKind.Utc),
                "CoinDesk",
                "SEC delays decision on spot ETH ETF amendment to June 5"),
        };

        var rendered = UserPromptRenderer.SentimentBatch(items);

        rendered.Should().StartWith("<news_items>\n");
        rendered.Should().Contain("<item ts=\"2026-04-25T08:12Z\" source=\"CoinDesk\">");
        rendered.Should().Contain("SEC delays decision on spot ETH ETF amendment to June 5");
        rendered.Should().EndWith("For each item, return one JSON object on its own line (NDJSON).");
    }

    [Fact]
    public void Sentiment_user_block_escapes_xml_special_chars()
    {
        var items = new List<NewsItem>
        {
            new(new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc),
                "Source<&>",
                "Headline with <angle> & ampersand"),
        };

        var rendered = UserPromptRenderer.SentimentBatch(items);
        rendered.Should().Contain("source=\"Source&lt;&amp;&gt;\"");
        rendered.Should().Contain("Headline with &lt;angle&gt; &amp; ampersand");
    }

    [Fact]
    public void Regime_user_block_matches_design_doc_layout()
    {
        var snap = new RegimeSnapshot(
            Symbol: "BTCUSDT",
            Interval: "1h",
            Adx14: 28.4m,
            PlusDi14: 24.1m,
            MinusDi14: 15.7m,
            Atr14: 412.5m,
            Atr50Sma: 380.2m,
            AtrRatio: 1.085m,
            BbWidthPct: 0.038m,
            BbWidthPct50pctl: 0.42m,
            Ema9: 64210m,
            Ema21: 63880m,
            Ema50: 63110m,
            Ema200: 58740m,
            Last20BarSlopePct: 0.18m,
            RuleRegime: Regime.TrendingUp,
            RuleConfidence: 0.72m,
            AsOfUtc: DateTime.UtcNow);

        var rendered = UserPromptRenderer.RegimeReadings(snap);
        rendered.Should().StartWith("Symbol: BTCUSDT  TF: 1h\n");
        rendered.Should().Contain("ADX(14)=28.4");
        rendered.Should().Contain("+DI=24.1  -DI=15.7");
        rendered.Should().Contain("ATR(14)=412.5  ATR50_SMA=380.2  ATR_ratio=1.085");
        rendered.Should().Contain("BBWidth_pct=0.038  BBWidth_pct_50pctl=0.42");
        rendered.Should().Contain("EMA(9)=64210.0  EMA(21)=63880.0  EMA(50)=63110.0  EMA(200)=58740.0");
        rendered.Should().Contain("Last 20 closes slope/bar: +0.18%");   // §5.4.2 example: 2dp
        rendered.Should().EndWith("Output JSON.");
    }

    [Fact]
    public void Setup_user_block_matches_design_doc_layout()
    {
        var ctx = new SetupContext(
            Strategy: "BREAKOUT_DON",
            Symbol:   "SOLUSDT",
            Side:     "BUY",
            Entry:    178.42m,
            StopLoss: 174.10m,
            TakeProfit: 187.06m,
            AtrMultipleStop: 1.5m,
            AtrMultipleTake: 3.0m,
            RuleRegime: "TRENDING_UP",
            RuleAdx:    31m,
            SentimentScore6h: 0.42m,
            SentimentItems6h: 3,
            BreakoutMagnitudePct: 0.38m,
            VolumeXSma20: 1.7m,
            Ema200DistancePct: 6.4m,
            StrategyHistorySummary: "3W/2L, avg R = +0.42",
            RuleConfidence: 0.62m);

        var rendered = UserPromptRenderer.SetupReview(ctx);
        rendered.Should().Contain("Strategy: BREAKOUT_DON  Symbol: SOLUSDT  Side: BUY\n");
        rendered.Should().Contain("Entry: 178.42  SL: 174.10 (1.5*ATR)  TP: 187.06 (3*ATR)\n");
        rendered.Should().Contain("Regime (rule): TRENDING_UP (ADX 31)\n");
        rendered.Should().Contain("News sentiment last 6h: +0.42 (3 items)\n");
        rendered.Should().Contain("Breakout magnitude: +0.38% above prior 20-bar high\n");
        rendered.Should().Contain("Volume confirmation: 1.7x SMA20\n");
        rendered.Should().Contain("EMA200 distance: +6.4%\n");   // §5.4.3 example: trailing zero stripped
        rendered.Should().Contain("Last 5 BREAKOUT_DON trades on this symbol: 3W/2L, avg R = +0.42\n");
        rendered.Should().EndWith("Concerns to consider: late entry, exhaustion, news risk in next 8h.");
    }
}
