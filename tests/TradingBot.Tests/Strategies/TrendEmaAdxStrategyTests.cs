using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Strategies;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// §3.3 TREND_EMA_ADX golden tests. Snapshot-prior, HTF, ADX, EMA50, and the
/// explosive-bar guard each get a dedicated test so a regression is precise.
/// </summary>
public sealed class TrendEmaAdxStrategyTests
{
    private static TrendEmaAdxStrategy Build(TrendEmaAdxOptions? opts = null)
    {
        var monitor = new StaticOptionsMonitor<TrendEmaAdxOptions>(opts ?? new TrendEmaAdxOptions());
        return new TrendEmaAdxStrategy(monitor, NullLogger<TrendEmaAdxStrategy>.Instance);
    }

    private static IndicatorSnapshot Htf(decimal ema200) =>
        StrategyFixtures.SnapshotAllPopulated(ema200: ema200);

    [Fact]
    public void Long_trend_hits_on_bullish_cross_with_full_alignment()
    {
        // Current: EMA9 > EMA21; prior: EMA9 ≤ EMA21 ⇒ bullish cross
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(
            ema9: 99m, ema21: 100m); // before cross

        // HTF: close > EMA200_4h ⇒ alignment OK
        var htf = Htf(ema200: 9_500m);

        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, barHigh: 105m, barLow: 95m, // range = 10 < 2.5×100 = 250
            priorSnapshot: prior, htfBarClose: 10_500m);

        var result = Build().Evaluate(snap, htf, Regime.TrendingUp, ctx);

        result.Should().NotBeNull();
        result!.StrategyCode.Should().Be(StrategyCodes.TrendEmaAdx);
        result.Side.Should().Be(Sides.Buy);
        result.EntryPrice.Should().Be(100m);
        // §4.3: SL = entry − 2×ATR, TP = entry + 5×ATR
        result.StopLoss.Should().Be(100m - 2.0m * 100m);
        result.TakeProfit.Should().Be(100m + 5.0m * 100m);
        result.Trail.Should().NotBeNull();
        result.Trail!.Mode.Should().Be(TrailMode.Ema21OrChandelier);
        result.TimeStopBars.Should().Be(120); // 5 days × 1h
    }

    [Fact]
    public void Long_trend_misses_when_no_crossover_this_bar()
    {
        // EMA9 already above EMA21 in BOTH bars ⇒ no fresh cross
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 105m, ema21: 100m);
        var ctx = StrategyFixtures.CtxAllPopulated(
            priorSnapshot: prior, htfBarClose: 10_500m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_when_close_below_ema50()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 200m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, priorSnapshot: prior, htfBarClose: 10_500m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_when_htf_alignment_fails()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);
        // HTF EMA200 above HTF close ⇒ HTF says we're below the 200, not a long.
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, priorSnapshot: prior, htfBarClose: 9_000m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_when_adx_below_threshold()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 22m); // < 25
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, priorSnapshot: prior, htfBarClose: 10_500m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_on_explosive_bar()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);

        // Bar range = 350 > 2.5 × ATR(14) = 250 ⇒ explosive guard fires.
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, barHigh: 250m, barLow: -100m,
            priorSnapshot: prior, htfBarClose: 10_500m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_when_prior_snapshot_unavailable()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var ctx = StrategyFixtures.CtxAllPopulated(priorSnapshot: null, htfBarClose: 10_500m);

        Build().Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_trend_misses_when_htf_snapshot_unavailable()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);
        var ctx = StrategyFixtures.CtxAllPopulated(priorSnapshot: prior, htfBarClose: 10_500m);

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Short_trend_hits_on_bearish_cross_with_inverted_alignment()
    {
        // EMA9 < EMA21 now; was ≥ before ⇒ bearish cross.
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 90m, ema21: 100m, ema50: 105m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 101m, ema21: 100m);

        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 100m, barHigh: 105m, barLow: 95m,
            priorSnapshot: prior, htfBarClose: 9_000m);

        var result = Build().Evaluate(snap, Htf(9_500m), Regime.TrendingDown, ctx);

        result.Should().NotBeNull();
        result!.Side.Should().Be(Sides.Sell);
        result.StopLoss.Should().Be(100m + 2.0m * 100m);
        result.TakeProfit.Should().Be(100m - 5.0m * 100m);
    }

    [Fact]
    public void Disabled_options_short_circuits_the_strategy()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema9: 110m, ema21: 100m, ema50: 95m, adx: 30m);
        var prior = StrategyFixtures.SnapshotAllPopulated(ema9: 99m, ema21: 100m);
        var ctx = StrategyFixtures.CtxAllPopulated(priorSnapshot: prior, htfBarClose: 10_500m);

        Build(new TrendEmaAdxOptions { Enabled = false })
            .Evaluate(snap, Htf(9_500m), Regime.TrendingUp, ctx)
            .Should().BeNull();
    }
}
