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
/// §3.2 MR_BB_VWAP golden tests. Each test exercises one gate so a regression
/// surfaces with a precise message.
/// </summary>
public sealed class MeanReversionBbVwapStrategyTests
{
    private static MeanReversionBbVwapStrategy Build(MeanReversionBbVwapOptions? opts = null)
    {
        var monitor = new StaticOptionsMonitor<MeanReversionBbVwapOptions>(opts ?? new MeanReversionBbVwapOptions());
        return new MeanReversionBbVwapStrategy(monitor, NullLogger<MeanReversionBbVwapStrategy>.Instance);
    }

    [Fact]
    public void Long_mean_reversion_hits_when_all_gates_pass()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 50m, atr50Sma: 50m,           // ratio 1.0 ⇒ no vol adjustment
            adx: 18m,                          // < 25
            rsi: 22m,                          // < 25 oversold
            bbLower: 10_000m, bbUpper: 11_000m,
            // VWAP value zone: Close > VWAP × (1 − 0.015). With VWAP=10_050 the
            // lower bound is 9_899.25, so Close=9_990 sits inside the zone.
            vwap: 10_050m);

        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 9_990m,                  // < BB lower
            close3BarsAgo: 9_980m);            // close > close[-3] ⇒ micro-confirm OK

        var result = Build().Evaluate(snap, null, Regime.Ranging, ctx);

        result.Should().NotBeNull();
        result!.StrategyCode.Should().Be(StrategyCodes.MeanReversionBbVwap);
        result.Side.Should().Be(Sides.Buy);
        result.EntryPrice.Should().Be(9_990m);
        // §4.3: SL = entry − 1×ATR, TP = entry + 1.5×ATR
        result.StopLoss.Should().Be(9_990m - 1.0m * 50m);
        result.TakeProfit.Should().Be(9_990m + 1.5m * 50m);
        // No trail for MR (§4.3)
        result.Trail.Should().BeNull();
        result.TimeStopBars.Should().Be(8); // §3.2
    }

    [Fact]
    public void Long_misses_when_close_above_bb_lower()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            adx: 18m, rsi: 22m, bbLower: 9_900m, vwap: 10_000m);
        // close above BB lower ⇒ no stretch
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_950m, close3BarsAgo: 9_900m);

        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_misses_when_rsi_not_oversold()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            adx: 18m, rsi: 30m, bbLower: 10_000m, vwap: 10_200m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_980m);

        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_misses_when_close_outside_vwap_value_zone()
    {
        // VWAP buffer 0.015 (default). close < vwap × 0.985.
        var snap = StrategyFixtures.SnapshotAllPopulated(
            adx: 18m, rsi: 22m, bbLower: 10_000m, vwap: 10_300m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_980m);

        // 9_990 < 10_300 × 0.985 = 10_145.5 ⇒ outside value zone (catching a knife)
        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_misses_when_adx_too_high()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            adx: 30m,                  // ≥ 25 cap
            rsi: 22m, bbLower: 10_000m, vwap: 10_200m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_980m);

        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_misses_when_micro_confirm_fails()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            adx: 18m, rsi: 22m, bbLower: 10_000m, vwap: 10_200m);
        // close < close[-3] ⇒ no 3-bar reversal
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_995m);

        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }

    [Fact]
    public void Short_mean_reversion_hits_when_overbought_and_above_bb_upper()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 50m, atr50Sma: 50m,           // ratio 1.0 ⇒ no vol adjustment
            adx: 18m, rsi: 80m,
            bbLower: 10_000m, bbUpper: 11_000m,
            // Short value zone: Close < VWAP × (1 + 0.015). With VWAP=10_950
            // the upper bound is 11_114.25, so Close=11_010 sits inside the zone.
            vwap: 10_950m);

        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 11_010m, close3BarsAgo: 11_020m);

        var result = Build().Evaluate(snap, null, Regime.Ranging, ctx);

        result.Should().NotBeNull();
        result!.Side.Should().Be(Sides.Sell);
        // SL above entry for shorts; TP below.
        result.StopLoss.Should().Be(11_010m + 1.0m * 50m);
        result.TakeProfit.Should().Be(11_010m - 1.5m * 50m);
    }

    [Fact]
    public void Disabled_options_short_circuits_the_strategy()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(adx: 18m, rsi: 22m, bbLower: 10_000m, vwap: 10_200m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_980m);

        Build(new MeanReversionBbVwapOptions { Enabled = false })
            .Evaluate(snap, null, Regime.Ranging, ctx)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_snapshot_is_warming_up()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(rsi: null);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 9_990m, close3BarsAgo: 9_980m);

        Build().Evaluate(snap, null, Regime.Ranging, ctx).Should().BeNull();
    }
}
