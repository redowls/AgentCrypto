using FluentAssertions;
using Prometheus;
using TradingBot.Observability.Metrics;
using Xunit;

namespace TradingBot.Tests.Observability;

public class PrometheusTradingMetricsTests
{
    [Fact]
    public async Task IncSignal_emits_signals_total_family()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.IncSignal("breakout", "BTCUSDT", "LONG");

        var text = await CollectAsText(registry);
        text.Should().Contain("tradingbot_signals_total");
        text.Should().MatchRegex(@"strategy=""breakout""[^\n]*symbol=""BTCUSDT""[^\n]*side=""LONG""[^\n]*\}\s+1");
    }

    [Fact]
    public async Task SetAccountEquity_emits_account_equity_gauge()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.SetAccountEquity(12345.67);

        (await CollectAsText(registry))
            .Should().MatchRegex(@"tradingbot_account_equity_usd\s+12345\.67");
    }

    [Fact]
    public async Task ObserveStrategyLatency_emits_histogram_buckets()
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var m = new PrometheusTradingMetrics(registry);
        m.ObserveStrategyLatency("trend", 5);
        m.ObserveStrategyLatency("trend", 50);

        var text = await CollectAsText(registry);
        text.Should().Contain("tradingbot_strategy_latency_ms_bucket");
        text.Should().Contain("tradingbot_strategy_latency_ms_count");
        text.Should().Contain("tradingbot_strategy_latency_ms_sum");
    }

    private static async Task<string> CollectAsText(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
