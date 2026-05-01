using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Domain.Enums;
using TradingBot.Exchange.Abstractions;
using Xunit;

namespace TradingBot.Tests.Exchange;

/// Live smoke tests against Binance Spot Testnet. Gated by the
/// BINANCE_TESTNET=true env var (plus key/secret).
///
/// These are intentionally NOT [Collection]'d to share the fixture across the
/// whole class; each test acquires fixture, runs, and the fixture is torn
/// down per test class via xUnit's IClassFixture lifecycle.
public sealed class BinanceSpotTestnetSmokeTests : IClassFixture<BinanceTestnetFixture>
{
    private readonly BinanceTestnetFixture _fx;

    public BinanceSpotTestnetSmokeTests(BinanceTestnetFixture fx) => _fx = fx;

    [BinanceTestnetFact]
    public async Task ExchangeInfo_returns_active_symbols()
    {
        var gw = _fx.Services.GetRequiredService<IBinanceGatewayResolver>().Get(AccountType.Spot);

        var info = await gw.GetExchangeInfoAsync(CancellationToken.None);

        info.Symbols.Should().NotBeEmpty();
        info.Symbols.Count.Should().BeGreaterThan(50,
            "the testnet catalogue is smaller than mainnet but well over 50 symbols");
    }

    [BinanceTestnetFact]
    public async Task GetKlines_returns_recent_history()
    {
        var gw = _fx.Services.GetRequiredService<IBinanceGatewayResolver>().Get(AccountType.Spot);

        var bars = await gw.GetKlinesAsync(
            "BTCUSDT", CandleIntervals.OneMinute,
            startUtc: null, endUtc: null, limit: 100, CancellationToken.None);

        bars.Count.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(100);
    }

    [BinanceTestnetFact]
    public async Task PlaceLimitOrder_far_from_market_then_cancel()
    {
        var gw = _fx.Services.GetRequiredService<IBinanceGatewayResolver>().Get(AccountType.Spot);

        // Pick a price far below market so the order never fills.
        var bars = await gw.GetKlinesAsync(
            "BTCUSDT", CandleIntervals.OneMinute, null, null, 1, CancellationToken.None);
        var lastClose = bars[^1].Close;
        var farPrice = decimal.Round(lastClose * 0.5m, 2, MidpointRounding.ToZero);

        var clientOrderId = $"smoke-{Guid.NewGuid():N}".Substring(0, 32);

        var placed = await gw.PlaceOrderAsync(new OrderRequest
        {
            Account = AccountType.Spot,
            Symbol = "BTCUSDT",
            Side = Sides.Buy,
            OrderType = OrderTypes.Limit,
            Quantity = 0.001m,
            Price = farPrice,
            TimeInForce = "GTC",
            ClientOrderId = clientOrderId,
            CorrelationId = Guid.NewGuid().ToString(),
        }, CancellationToken.None);

        placed.Status.Should().BeOneOf(OrderStatuses.New, OrderStatuses.PartiallyFilled);

        var cancelled = await gw.CancelOrderAsync("BTCUSDT", clientOrderId, CancellationToken.None);
        cancelled.Status.Should().Be(OrderStatuses.Cancelled);
    }

    [BinanceTestnetFact]
    public async Task SubscribeKline_receives_a_message_within_90s()
    {
        var ws = _fx.Services.GetRequiredService<IBinanceWebSocketManager>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var received = new TaskCompletionSource<KlineData>();

        await using var sub = await ws.SubscribeKlineAsync(
            AccountType.Spot, "BTCUSDT", CandleIntervals.OneMinute,
            kline =>
            {
                received.TrySetResult(kline);
                return ValueTask.CompletedTask;
            },
            cts.Token);

        var winner = await Task.WhenAny(received.Task, Task.Delay(Timeout.Infinite, cts.Token));
        winner.Should().Be(received.Task, "expected at least one kline message within 90s");
    }
}
