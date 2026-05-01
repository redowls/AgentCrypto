using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;
using TradingBot.Tests.Exchange;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Live testnet smoke test for §4 DoD: subscribe BTCUSDT 5m for ~20 minutes,
/// verify ≥4 closed bars arrive on the channel.
///
/// We don't bring SQL or Redis online here — that's the job of the broader
/// integration harness (Worker boot test). This test isolates "WS handler →
/// channel → reader" so a CI failure points at the ingestion seam, not a DB
/// container hiccup.
///
/// Auto-skips unless <c>BINANCE_TESTNET=true</c> + key env vars are set.
/// </summary>
[Collection("BinanceTestnetSerial")]
public sealed class MarketDataTestnetSmokeTests : IClassFixture<BinanceTestnetFixture>
{
    private readonly BinanceTestnetFixture _fx;

    public MarketDataTestnetSmokeTests(BinanceTestnetFixture fx)
    {
        _fx = fx;
    }

    [BinanceTestnetFact(Timeout = 25 * 60 * 1000)]
    public async Task Subscribe_5m_For_20min_Yields_AtLeast_4_ClosedBars()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(22));

        // Channel + WS manager are wired by the existing testnet fixture.
        var ws = (IBinanceWebSocketManager)_fx.Services.GetService(typeof(IBinanceWebSocketManager))!;
        var channel = new BoundedKlineChannel(Options.Create(new MarketDataOptions { ChannelCapacity = 1024 }));

        // Stream BTCUSDT 5m kline → channel.
        await using var sub = await ws.SubscribeKlineAsync(
            AccountType.Spot, "BTCUSDT", "5m",
            async k =>
            {
                var evt = new KlineEvent(
                    SymbolId: 0, Symbol: "BTCUSDT", Interval: "5m",
                    Account: AccountType.Spot, Kline: k, Source: KlineSource.WebSocket);
                await channel.Writer.WriteAsync(evt, cts.Token);
            },
            cts.Token);

        var closedBars = new List<DateTime>();
        try
        {
            while (await channel.Reader.WaitToReadAsync(cts.Token))
            {
                while (channel.Reader.TryRead(out var evt))
                {
                    if (evt.Kline.IsClosed)
                    {
                        closedBars.Add(evt.Kline.OpenTimeUtc);
                        if (closedBars.Count >= 4)
                        {
                            cts.Cancel(); // we have enough — exit early
                            return;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected exit */ }

        closedBars.Should().HaveCountGreaterThanOrEqualTo(4,
            "20+ minutes of BTCUSDT 5m must produce at least 4 closed bars");

        // Bars must be strictly ordered and 5m apart.
        for (var i = 1; i < closedBars.Count; i++)
        {
            (closedBars[i] - closedBars[i - 1]).Should().Be(TimeSpan.FromMinutes(5),
                $"successive 5m closes must be exactly 5m apart (i={i})");
        }

        // Suppress unused-warning on the logger since this is a smoke test that
        // intentionally does not assert log messages.
        ILogger _ = NullLogger.Instance;
    }
}

[CollectionDefinition("BinanceTestnetSerial", DisableParallelization = true)]
public sealed class BinanceTestnetSerial { }
