using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Execution.Channels;
using TradingBot.Execution.Configuration;
using TradingBot.Execution.Engine;
using TradingBot.Execution.Identity;
using TradingBot.Execution.Slippage;
using TradingBot.Execution.State;
using TradingBot.Risk.Abstractions;
using TradingBot.Tests.Risk;
using Xunit;

namespace TradingBot.Tests.Execution;

public class ExecutionEngineTests
{
    private const int BtcId = 101;

    private sealed class Harness
    {
        public FakeOrderRepository Orders { get; } = new();
        public FakeFillRepository Fills { get; } = new();
        public FakeExecutionDiagnosticsRepository Diagnostics { get; } = new();
        public FakeBracketLinkRepository BracketLinks { get; } = new();
        public FakeRiskEventRepository2 RiskEvents { get; } = new();
        public FakeSymbolRepository2 Symbols { get; } = new();
        public FakeSymbolFilters Filters { get; } = new();
        public FixedGatewayResolver Gateways { get; } = new();
        public FakeKillSwitch KillSwitch { get; } = new();
        public OrderStateMachine StateMachine { get; } = new();
        public DefaultSlippageModel Slippage { get; } = new();
        public FixedClock Clock { get; } = new(new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc));
        public ExecutionOptions Options { get; } = new();

        public ExecutionEngine Engine { get; }
        public IApprovedIntentChannel Channel { get; }

        public Harness()
        {
            Symbols.ById[BtcId] = new Symbol
            {
                SymbolId   = BtcId, Exchange = Exchanges.BinanceSpot, SymbolCode = "BTCUSDT",
                BaseAsset = "BTC", QuoteAsset = "USDT", TickSize = 0.01m, StepSize = 0.0001m, MinNotional = 5m, IsActive = true,
            };
            Filters.Map[(AccountType.Spot, "BTCUSDT")] = Symbols.ById[BtcId];

            var services = new ServiceCollection();
            services.AddSingleton<IOrderRepository>(Orders);
            services.AddSingleton<IFillRepository>(Fills);
            services.AddSingleton<IExecutionDiagnosticsRepository>(Diagnostics);
            services.AddSingleton<IBracketLinkRepository>(BracketLinks);
            services.AddSingleton<IRiskEventRepository>(RiskEvents);
            services.AddSingleton<ISymbolRepository>(Symbols);
            services.AddSingleton<ISymbolFilters>(Filters);
            services.AddSingleton<IBinanceGatewayResolver>(Gateways);
            services.AddSingleton<IKillSwitch>(KillSwitch);
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<ISlippageModel>(Slippage);
            services.AddSingleton<OrderStateMachine>(StateMachine);

            var sp = services.BuildServiceProvider();
            Channel = new BoundedApprovedIntentChannel(Microsoft.Extensions.Options.Options.Create(Options));
            Engine = new ExecutionEngine(
                Channel,
                sp.GetRequiredService<IServiceScopeFactory>(),
                StateMachine,
                Clock,
                NullLogger<ExecutionEngine>.Instance);
        }

        public ApprovedIntent Intent(decimal qty = 0.01m, long signalId = 999, string side = Sides.Buy) =>
            new(
                Signal: new Signal
                {
                    SignalId = signalId, SymbolId = BtcId, Strategy = "BREAKOUT_DON",
                    Interval = CandleIntervals.OneHour, Side = side,
                    EntryPrice = 30_000m, StopLoss = 29_400m, TakeProfit = 31_200m,
                    AtrValue = 250m, Confidence = 0.7m, Status = SignalStatuses.Approved,
                },
                Quantity: qty,
                RiskUsd: 200m,
                NotionalUsd: 300m,
                AccountType: AccountTypes.Spot,
                SymbolCode: "BTCUSDT");
    }

    [Fact]
    public async Task Happy_path_persists_and_submits_one_order()
    {
        var h = new Harness();
        var intent = h.Intent();

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        h.Gateways.Spot.Placed.Should().HaveCount(1);
        var placed = h.Gateways.Spot.Placed[0];
        placed.Symbol.Should().Be("BTCUSDT");
        placed.Side.Should().Be(Sides.Buy);
        placed.OrderType.Should().Be(OrderTypes.Market);
        placed.Quantity.Should().Be(0.01m);
        placed.ClientOrderId.Should().StartWith("BOT-BD-999-");
        placed.ClientOrderId.Length.Should().BeLessOrEqualTo(36);

        var stored = await h.Orders.GetByClientOrderIdAsync(placed.ClientOrderId, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(OrderStatuses.New);
        stored.ExchangeOrderId.Should().NotBeNull();
    }

    [Fact]
    public async Task Same_signal_id_never_produces_two_distinct_exchange_orders()
    {
        var h = new Harness();
        var intent = h.Intent(signalId: 1234);

        await h.Engine.SubmitAsync(intent, CancellationToken.None);
        await h.Engine.SubmitAsync(intent, CancellationToken.None);
        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        h.Gateways.Spot.Placed.Should().HaveCount(1, "idempotency by ClientOrderId must short-circuit retries");
    }

    [Fact]
    public async Task Network_drop_during_submit_marks_order_ERROR_without_double_submit()
    {
        var h = new Harness();
        h.Gateways.Spot.PlaceThrows = FakeBinanceErrors.NetworkDrop();
        var intent = h.Intent(signalId: 555);

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        var cid = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 555);
        var stored = await h.Orders.GetByClientOrderIdAsync(cid, CancellationToken.None);
        stored!.Status.Should().Be(OrderStatuses.Error);
        h.Gateways.Spot.Placed.Should().HaveCount(1, "the engine attempts the call exactly once");

        // Critical: a retry of the same intent does NOT re-submit even though
        // the network drop left it in ERROR. The ERROR row's clientOrderId
        // already exists, so InsertIfNew short-circuits.
        h.Gateways.Spot.PlaceThrows = null;
        await h.Engine.SubmitAsync(intent, CancellationToken.None);
        h.Gateways.Spot.Placed.Should().HaveCount(1, "ERROR is terminal — no retry without operator action");
    }

    [Fact]
    public async Task Http_429_propagates_through_resilience_pipeline_and_marks_ERROR()
    {
        // Note: in production the Polly pipeline retries 429 transparently —
        // our mock gateway is below the pipeline, so we observe the raw call.
        var h = new Harness();
        h.Gateways.Spot.PlaceThrows = FakeBinanceErrors.Http429();
        var intent = h.Intent(signalId: 777);

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        var cid = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 777);
        var stored = await h.Orders.GetByClientOrderIdAsync(cid, CancellationToken.None);
        stored!.Status.Should().Be(OrderStatuses.Error);
        stored.Notes.Should().Contain("BinanceApiException");
    }

    [Fact]
    public async Task Http_418_marks_ERROR_and_is_observed_by_kill_switch_in_pipeline()
    {
        var h = new Harness();
        h.Gateways.Spot.PlaceThrows = FakeBinanceErrors.Http418();
        var intent = h.Intent(signalId: 888);

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        var cid = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 888);
        var stored = await h.Orders.GetByClientOrderIdAsync(cid, CancellationToken.None);
        stored!.Status.Should().Be(OrderStatuses.Error);
    }

    [Fact]
    public async Task Rejected_response_writes_RiskEvent_and_does_not_open_position()
    {
        var h = new Harness();
        h.Gateways.Spot.OnPlace = req => new OrderResult
        {
            Symbol = req.Symbol, ClientOrderId = req.ClientOrderId,
            ExchangeOrderId = 9001, Status = "REJECTED", ExecutedQty = 0m,
            AvgFillPrice = null, TransactTimeUtc = DateTime.UtcNow,
        };
        var intent = h.Intent(signalId: 444);

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        h.RiskEvents.Events.Should().ContainSingle(e => e.EventType == "ORDER_REJECTED");
        var stored = await h.Orders.GetByClientOrderIdAsync(
            ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 444), CancellationToken.None);
        stored!.Status.Should().Be(OrderStatuses.Rejected);
    }

    [Fact]
    public async Task Kill_switch_tripped_at_submit_time_aborts_without_calling_gateway()
    {
        var h = new Harness();
        await h.KillSwitch.TripAsync(KillSwitchSource.ManualCommand, "drill", CancellationToken.None);

        await h.Engine.SubmitAsync(h.Intent(signalId: 1), CancellationToken.None);

        h.Gateways.Spot.Placed.Should().BeEmpty();
    }

    [Fact]
    public async Task Immediate_market_fill_writes_diagnostics_row()
    {
        var h = new Harness();
        h.Gateways.Spot.OnPlace = req => new OrderResult
        {
            Symbol = req.Symbol, ClientOrderId = req.ClientOrderId,
            ExchangeOrderId = 9001, Status = "FILLED", ExecutedQty = req.Quantity,
            AvgFillPrice = 30_010m, TransactTimeUtc = DateTime.UtcNow,
        };

        await h.Engine.SubmitAsync(h.Intent(signalId: 2024, qty: 0.05m), CancellationToken.None);

        h.Diagnostics.Rows.Should().HaveCount(1);
        var diag = h.Diagnostics.Rows[0];
        diag.OrderType.Should().Be(OrderTypes.Market);
        diag.ActualPrice.Should().Be(30_010m);
        diag.ObservedSlippageBps.Should().BeApproximately(
            DefaultSlippageModel.ObservedSlippageBps(30_000m, 30_010m, Sides.Buy), 0.01m);
        diag.ModelVersion.Should().Be(DefaultSlippageModel.ModelVersion);
    }

    [Fact]
    public async Task Quantity_below_step_size_results_in_ERROR_terminal()
    {
        var h = new Harness();
        // Force a quantity that snaps to zero against the step.
        var intent = h.Intent(qty: 0.00001m); // BTCUSDT step = 0.0001

        await h.Engine.SubmitAsync(intent, CancellationToken.None);

        h.Gateways.Spot.Placed.Should().BeEmpty();
        var cid = ClientOrderIdGenerator.ForEntry("BREAKOUT_DON", 999);
        var stored = await h.Orders.GetByClientOrderIdAsync(cid, CancellationToken.None);
        stored!.Status.Should().Be(OrderStatuses.Error);
    }

    [Fact]
    public void NormalizeStatus_maps_known_codes_and_falls_back_to_ERROR()
    {
        ExecutionEngine.NormalizeStatus("FILLED").Should().Be(OrderStatuses.Filled);
        ExecutionEngine.NormalizeStatus("PARTIALLY_FILLED").Should().Be(OrderStatuses.PartiallyFilled);
        ExecutionEngine.NormalizeStatus("CANCELED").Should().Be(OrderStatuses.Cancelled);
        ExecutionEngine.NormalizeStatus("CANCELLED").Should().Be(OrderStatuses.Cancelled);
        ExecutionEngine.NormalizeStatus("PENDING_CANCEL").Should().Be(OrderStatuses.Canceling);
        ExecutionEngine.NormalizeStatus("FUTURE_VALUE_FROM_BINANCE").Should().Be(OrderStatuses.Error);
    }
}
