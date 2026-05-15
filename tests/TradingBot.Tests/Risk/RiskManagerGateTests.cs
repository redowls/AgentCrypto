using FluentAssertions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Risk.Abstractions;
using Xunit;

namespace TradingBot.Tests.Risk;

/// <summary>
/// Per-gate unit tests for §8.5. Each test isolates a single failure mode by
/// setting up a happy-path baseline and then perturbing exactly one input.
/// </summary>
public sealed class RiskManagerGateTests
{
    [Fact]
    public async Task Approves_happy_path_signal_with_full_size()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var s  = RiskTestFixtures.Signal();
        var st = RiskTestFixtures.State(equity: 20_000m);

        var result = await rm.ApproveAsync(s, st, CancellationToken.None);

        result.Approved.Should().BeTrue("baseline must pass every gate");
        result.RejectReason.Should().BeNull();
        result.Quantity.Should().BeGreaterThan(0m);
        // Risk = 20,000 × 0.01 × 1.0 × 1.0 = 200. SL distance = 600 ⇒ qty ≈ 0.3333
        // step = 0.0001 ⇒ 0.3333 (clamped).
        result.RiskUsd.Should().Be(200m);
        result.KFactor.Should().Be(1.00m);
        result.VolAdjust.Should().Be(1.00m);
    }

    // ---------------- Gate (a): Daily loss limit ----------------------------------

    [Fact]
    public async Task Rejects_when_daily_pnl_at_or_below_minus_three_pct()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var st = RiskTestFixtures.State(equity: 19_400m, equityAt00: 20_000m, dailyPnl: -0.03m);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), st, CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.DailyLossLimit);
        h.RiskEvents.Events.Should().ContainSingle(e => e.EventType == RiskRejectReasons.DailyLossLimit);
    }

    [Fact]
    public async Task Approves_when_daily_pnl_just_above_threshold()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var st = RiskTestFixtures.State(dailyPnl: -0.0299m);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), st, CancellationToken.None);

        result.Approved.Should().BeTrue();
    }

    // ---------------- Gate (b): Max drawdown halt --------------------------------

    [Fact]
    public async Task Rejects_when_drawdown_at_or_below_minus_fifteen_pct()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var st = RiskTestFixtures.State(equity: 17_000m, hwm: 20_000m, drawdown: -0.15m);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), st, CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.MaxDrawdownHalt);
    }

    // ---------------- Gate (c): Concurrent positions ------------------------------

    [Fact]
    public async Task Rejects_when_open_positions_at_or_above_cap()
    {
        var h = new RiskManagerHarness();
        for (var i = 0; i < 4; i++)
        {
            h.Positions.Open.Add(new Position
            {
                PositionId    = i + 1,
                SymbolId      = RiskTestFixtures.BtcId + i,
                AccountType   = AccountTypes.Spot,
                Side          = PositionSides.Long,
                Quantity      = 0.1m,
                AvgEntryPrice = 30_000m,
                StopLoss      = 29_400m,
                TakeProfit    = 31_200m,
                OpenedAt      = h.Clock.UtcNow,
                Status        = PositionStatuses.Open,
            });
        }
        var rm = h.Build();

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), RiskTestFixtures.State(), CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.MaxConcurrentPositions);
    }

    // ---------------- Gate (d): Correlation cluster -------------------------------

    [Fact]
    public async Task Rejects_when_correlation_cluster_already_occupied_on_same_side()
    {
        var h = new RiskManagerHarness();
        h.Correlation.ReturnOccupied = true;
        var rm = h.Build();

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), RiskTestFixtures.State(), CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.CorrelationClusterOccupied);
    }

    // ---------------- Gate (e): Drawdown ladder kFactor ---------------------------

    [Theory]
    [InlineData(-0.04, 1.00)]
    [InlineData(-0.05, 1.00)]   // boundary inclusive on the upper rung
    [InlineData(-0.07, 0.50)]
    [InlineData(-0.12, 0.25)]
    public async Task Sizes_with_drawdown_ladder_multiplier(double drawdown, double expectedK)
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var st = RiskTestFixtures.State(
            equity: 19_000m,
            hwm: 20_000m,
            drawdown: (decimal)drawdown);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), st, CancellationToken.None);

        result.Approved.Should().BeTrue();
        result.KFactor.Should().Be((decimal)expectedK);
    }

    // ---------------- Gate (k): Gross exposure cap -------------------------------

    [Fact]
    public async Task Rejects_when_gross_exposure_exceeds_two_x_equity_after_clamp()
    {
        // Equity 10k; existing gross 19k; new ~6k notional → 25k > 20k.
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var st = RiskTestFixtures.State(
            equity: 10_000m,
            gross:  19_000m,
            accountType: AccountTypes.UmFut);
        var s  = RiskTestFixtures.Signal(side: Sides.Buy, entry: 30_000m, sl: 29_400m, tp: 31_200m);

        var result = await rm.ApproveAsync(s, st, CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.GrossExposure);
    }

    // ---------------- Gate (j): Single-symbol cap (clamped, not rejected) ---------

    [Fact]
    public async Task Single_symbol_cap_clamps_quantity_to_50pct_of_equity()
    {
        // Tiny SL distance = huge raw qty → clamp to 50% of equity.
        var opts = RiskTestFixtures.DefaultOptions();
        var h = new RiskManagerHarness(opts);
        var rm = h.Build();
        var st = RiskTestFixtures.State(equity: 20_000m);
        var s  = RiskTestFixtures.Signal(entry: 30_000m, sl: 29_995m, tp: 30_100m, atr: 5m);

        var result = await rm.ApproveAsync(s, st, CancellationToken.None);

        result.Approved.Should().BeTrue();
        // Notional should sit at or just below 50% of equity (= 10,000).
        result.NotionalUsd.Should().BeLessThanOrEqualTo(10_000m);
        result.NotionalUsd.Should().BeGreaterThan(9_000m,
            "the clamp should target the cap, not zero it out");
    }

    // ---------------- Gate (l): Funding-rate veto --------------------------------

    [Fact]
    public async Task Rejects_long_when_funding_is_positive_above_threshold_within_window()
    {
        var h = new RiskManagerHarness();
        h.Funding.Map[RiskTestFixtures.Btc] = new FundingRateSnapshot(
            SymbolCode:        RiskTestFixtures.Btc,
            Rate:              0.001m, // 0.1% — well above 0.05% default
            NextFundingTimeUtc: h.Clock.UtcNow.AddMinutes(10),
            ObservedAtUtc:     h.Clock.UtcNow);
        var rm = h.Build();
        var st = RiskTestFixtures.State(accountType: AccountTypes.UmFut);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(side: Sides.Buy), st, CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.FundingRateHostile);
    }

    [Fact]
    public async Task Approves_short_when_funding_positive_pays_long_side()
    {
        var h = new RiskManagerHarness();
        h.Funding.Map[RiskTestFixtures.Btc] = new FundingRateSnapshot(
            SymbolCode:        RiskTestFixtures.Btc,
            Rate:              0.001m,
            NextFundingTimeUtc: h.Clock.UtcNow.AddMinutes(10),
            ObservedAtUtc:     h.Clock.UtcNow);
        var rm = h.Build();
        var st = RiskTestFixtures.State(accountType: AccountTypes.UmFut);
        // Short with positive funding ⇒ shorts are PAID (not paying), not hostile.
        var s = RiskTestFixtures.Signal(side: Sides.Sell, entry: 30_000m, sl: 30_600m, tp: 28_800m);

        var result = await rm.ApproveAsync(s, st, CancellationToken.None);

        result.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Funding_veto_skipped_for_spot_account()
    {
        var h = new RiskManagerHarness();
        h.Funding.Map[RiskTestFixtures.Btc] = new FundingRateSnapshot(
            RiskTestFixtures.Btc, 0.01m, h.Clock.UtcNow.AddMinutes(10), h.Clock.UtcNow);
        var rm = h.Build();
        var st = RiskTestFixtures.State(accountType: AccountTypes.Spot);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(side: Sides.Buy), st, CancellationToken.None);

        result.Approved.Should().BeTrue("funding veto applies to futures only per §8.2");
    }

    [Fact]
    public async Task Funding_veto_skipped_when_next_tick_outside_window()
    {
        var h = new RiskManagerHarness();
        h.Funding.Map[RiskTestFixtures.Btc] = new FundingRateSnapshot(
            SymbolCode:        RiskTestFixtures.Btc,
            Rate:              0.001m,
            NextFundingTimeUtc: h.Clock.UtcNow.AddHours(2), // > 30m default window
            ObservedAtUtc:     h.Clock.UtcNow);
        var rm = h.Build();
        var st = RiskTestFixtures.State(accountType: AccountTypes.UmFut);

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(side: Sides.Buy), st, CancellationToken.None);

        result.Approved.Should().BeTrue();
    }

    // ---------------- Pre-gate: Kill switch --------------------------------------

    [Fact]
    public async Task Rejects_immediately_when_kill_switch_tripped()
    {
        var h = new RiskManagerHarness();
        h.KillSwitch.IsTripped = true;
        h.KillSwitch.Reason = "manual";
        var rm = h.Build();

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), RiskTestFixtures.State(), CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.KillSwitchActive);
        // Pre-gate must short-circuit BEFORE we ask correlation about the cluster.
        h.Correlation.CallCount.Should().Be(0);
    }

    // ---------------- Sanity: zero stop distance ---------------------------------

    [Fact]
    public async Task Rejects_when_stop_distance_is_zero()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var s  = RiskTestFixtures.Signal(entry: 30_000m, sl: 30_000m);

        var result = await rm.ApproveAsync(s, RiskTestFixtures.State(), CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.PreconditionFailed);
    }
}
