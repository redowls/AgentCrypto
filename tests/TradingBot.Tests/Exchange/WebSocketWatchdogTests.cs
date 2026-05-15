using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;
using TradingBot.Exchange.WebSocket;
using Xunit;

namespace TradingBot.Tests.Exchange;

public sealed class WebSocketWatchdogTests
{
    [Fact]
    public async Task Stale_stream_raises_alert_once()
    {
        var registry = new StreamRegistry();
        var sink = new RecordingAlertSink();
        var options = Options.Create(new BinanceOptions
        {
            WebSocketStaleAfter = TimeSpan.FromMilliseconds(50),
            WebSocketWatchdogInterval = TimeSpan.FromMilliseconds(20),
        });

        var rec = registry.Register("spot.kline.BTCUSDT.1m", AccountType.Spot);
        rec.MarkEvent();

        var watchdog = new WebSocketWatchdog(registry, sink,
            new TestOptionsMonitor<BinanceOptions>(options.Value),
            new TradingBot.Core.Observability.NullTradingMetrics(),
            NullLogger<WebSocketWatchdog>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = watchdog.StartAsync(cts.Token);

        // Wait long enough for the stream to age past the stale threshold and
        // for at least two watchdog ticks. The watchdog should fire exactly once.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await watchdog.StopAsync(CancellationToken.None);
        await run;

        sink.StaleAlerts.Should().HaveCount(1);
        sink.StaleAlerts[0].StreamId.Should().Be("spot.kline.BTCUSDT.1m");
    }

    [Fact]
    public async Task Recovered_stream_clears_so_alert_fires_again_on_re_stale()
    {
        var registry = new StreamRegistry();
        var sink = new RecordingAlertSink();
        var options = Options.Create(new BinanceOptions
        {
            WebSocketStaleAfter = TimeSpan.FromMilliseconds(50),
            WebSocketWatchdogInterval = TimeSpan.FromMilliseconds(20),
        });

        var rec = registry.Register("spot.kline.ETHUSDT.1m", AccountType.Spot);
        rec.MarkEvent();

        var watchdog = new WebSocketWatchdog(registry, sink,
            new TestOptionsMonitor<BinanceOptions>(options.Value),
            new TradingBot.Core.Observability.NullTradingMetrics(),
            NullLogger<WebSocketWatchdog>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = watchdog.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        sink.StaleAlerts.Should().HaveCount(1);

        // Recover.
        rec.MarkEvent();
        await Task.Delay(TimeSpan.FromMilliseconds(80));
        // Then stale again.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await watchdog.StopAsync(CancellationToken.None);
        await run;

        sink.StaleAlerts.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    private sealed class RecordingAlertSink : IWebSocketAlertSink
    {
        public List<StreamHealth> StaleAlerts { get; } = new();
        public List<AccountType> ListenKeyExpiries { get; } = new();
        public void RaiseStaleStream(StreamHealth health) { lock (StaleAlerts) StaleAlerts.Add(health); }
        public void RaiseListenKeyExpired(AccountType account) { lock (ListenKeyExpiries) ListenKeyExpiries.Add(account); }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
