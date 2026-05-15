using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Exchange.Abstractions;
using TradingBot.Execution.Brackets;
using TradingBot.Tests.Risk;
using Xunit;

namespace TradingBot.Tests.Execution;

public class BracketPlacerTests
{
    private const int BtcId = 101;

    private sealed class Harness
    {
        public FakeOrderRepository Orders { get; } = new();
        public FakeBracketLinkRepository Links { get; } = new();
        public FakeSymbolFilters Filters { get; } = new();
        public FixedGatewayResolver Gateways { get; } = new();
        public FixedClock Clock { get; } = new(new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc));

        public FuturesEmulatedBracketPlacer Futures { get; }

        public Harness()
        {
            var btc = new Symbol
            {
                SymbolId = BtcId, SymbolCode = "BTCUSDT", BaseAsset = "BTC", QuoteAsset = "USDT",
                Exchange = Exchanges.BinanceUmFut, TickSize = 0.01m, StepSize = 0.0001m, MinNotional = 5m, IsActive = true,
            };
            Filters.Map[(AccountType.UmFutures, "BTCUSDT")] = btc;

            Futures = new FuturesEmulatedBracketPlacer(Gateways, Filters, Orders, Links, Clock,
                NullLogger<FuturesEmulatedBracketPlacer>.Instance);
        }
    }

    [Fact]
    public async Task Futures_PlaceAsync_submits_two_reduceOnly_orders_and_writes_link()
    {
        var h = new Harness();
        var req = new BracketPlacementRequest(
            PositionId: 10, SignalId: 100, AccountType: AccountTypes.UmFut,
            SymbolCode: "BTCUSDT", SymbolId: BtcId, EntrySide: Sides.Buy,
            EntryFillPrice: 30_000m, Quantity: 0.01m,
            StopLossPrice: 29_400m, TakeProfitPrice: 31_200m, CorrelationId: "100");

        var result = await h.Futures.PlaceAsync(req, CancellationToken.None);

        h.Gateways.Futures.Placed.Should().HaveCount(2);
        h.Gateways.Futures.Placed.Should().OnlyContain(r => r.ReduceOnly);
        h.Gateways.Futures.Placed.Should().OnlyContain(r => r.Side == Sides.Sell, "exit side is opposite of entry");
        h.Gateways.Futures.Placed.Select(r => r.OrderType).Should()
            .Contain(OrderTypes.StopMarket).And.Contain(OrderTypes.TakeProfitMarket);
        h.Links.Links.Should().HaveCount(1);
        h.Links.Links[0].Status.Should().Be("ACTIVE");
        result.AccountType.Should().Be(AccountTypes.UmFut);
    }

    [Fact]
    public async Task Sibling_cancellation_only_one_winner_under_concurrent_fills()
    {
        var h = new Harness();
        await h.Futures.PlaceAsync(new BracketPlacementRequest(
            PositionId: 20, SignalId: 200, AccountType: AccountTypes.UmFut,
            SymbolCode: "BTCUSDT", SymbolId: BtcId, EntrySide: Sides.Buy,
            EntryFillPrice: 30_000m, Quantity: 0.01m,
            StopLossPrice: 29_400m, TakeProfitPrice: 31_200m, CorrelationId: "200"),
            CancellationToken.None);

        var link = h.Links.Links.Single();
        // Two reactors race: SL fill arrives, and at the same moment the TP
        // also reports fill (impossible at the exchange but the userData
        // delivery order is non-deterministic at the SDK boundary).
        var slFill = new BracketLegFilled(link.StopClientOrderId, "SL", link.StopOrderId, 20, AccountTypes.UmFut, "BTCUSDT", "200");
        var tpFill = new BracketLegFilled(link.TpClientOrderId,   "TP", link.TakeProfitOrderId, 20, AccountTypes.UmFut, "BTCUSDT", "200");

        var t1 = h.Futures.HandleLegFilledAsync(slFill, CancellationToken.None);
        var t2 = h.Futures.HandleLegFilledAsync(tpFill, CancellationToken.None);
        await Task.WhenAll(t1, t2);

        h.Gateways.Futures.Cancelled.Should().HaveCount(1, "exactly one sibling-cancel attempted (CAS reservation)");
        h.Links.Links.Single().Status.Should().Be("RESOLVED");
    }

    [Fact]
    public async Task UpdateStopAsync_places_new_SL_before_cancelling_old_one()
    {
        var h = new Harness();
        await h.Futures.PlaceAsync(new BracketPlacementRequest(
            PositionId: 30, SignalId: 300, AccountType: AccountTypes.UmFut,
            SymbolCode: "BTCUSDT", SymbolId: BtcId, EntrySide: Sides.Buy,
            EntryFillPrice: 30_000m, Quantity: 0.01m,
            StopLossPrice: 29_400m, TakeProfitPrice: 31_200m, CorrelationId: "300"),
            CancellationToken.None);

        h.Gateways.Futures.Placed.Clear();
        h.Gateways.Futures.Cancelled.Clear();

        await h.Futures.UpdateStopAsync(new BracketUpdateRequest(
            PositionId: 30, SignalId: 300, Sequence: 1, AccountType: AccountTypes.UmFut,
            SymbolCode: "BTCUSDT", SymbolId: BtcId, PositionSide: PositionSides.Long,
            Quantity: 0.01m, NewStopLossPrice: 29_700m, ExistingTakeProfitPrice: 31_200m,
            CorrelationId: "300"), CancellationToken.None);

        h.Gateways.Futures.Placed.Should().HaveCount(1, "the new SL is placed first");
        h.Gateways.Futures.Cancelled.Should().HaveCount(1, "old SL is cancelled after the replacement is live");
        h.Links.Links.Should().HaveCount(2);
        h.Links.Links[0].Status.Should().Be("RESOLVED");
        h.Links.Links[1].Status.Should().Be("ACTIVE");
        h.Links.Links[1].StopClientOrderId.Should().StartWith("BOT-TR001-");
    }
}
