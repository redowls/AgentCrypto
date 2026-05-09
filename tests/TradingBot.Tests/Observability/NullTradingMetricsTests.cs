using FluentAssertions;
using TradingBot.Core.Observability;
using Xunit;

namespace TradingBot.Tests.Observability;

public class NullTradingMetricsTests
{
    [Fact]
    public void All_methods_are_no_ops()
    {
        ITradingMetrics m = new NullTradingMetrics();

        // Each call must complete without throwing.
        var act = () =>
        {
            m.IncSignal("s", "BTCUSDT", "LONG");
            m.IncOrder("FILLED", "BUY", "BTCUSDT");
            m.IncOrderFilled("BUY", "BTCUSDT");
            m.IncOrderCanceled("SELL", "ETHUSDT");
            m.SetPositionPnl("BTCUSDT", 12.34);
            m.SetAccountEquity(10_000);
            m.SetDrawdown(-0.05);
            m.IncAiCall("setup", "ok");
            m.AddAiCost("regime", 0.001);
            m.IncWsReconnect("Spot", "kline");
            m.ObserveStrategyLatency("breakout", 12.3);
            m.ObserveOrderFillLatency("BUY", "BTCUSDT", 250);
            m.SetWsLastEventSeconds("Spot", "userData", 0.5);
            m.IncAlertDeduped("Warn");
        };
        act.Should().NotThrow();
    }
}
