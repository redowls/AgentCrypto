using FluentAssertions;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Brackets;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Property-style tests for <see cref="BracketCalculator"/> — invariants every
/// bracket must satisfy regardless of the input it was computed from. We rely
/// on xUnit's <c>[Theory]</c> with a hand-rolled value matrix instead of FsCheck;
/// the package isn't pulled into the test project and the parameter space is
/// small enough that a curated matrix exercises the same boundaries.
/// </summary>
public sealed class BracketCalculatorTests
{
    public static IEnumerable<object[]> EntryAtrCases()
    {
        // (entry, atr14, atr50sma)
        yield return new object[] { 100m,    1m,      1m };       // tiny ATR
        yield return new object[] { 100m,    50m,     50m };      // proportional
        yield return new object[] { 30_000m, 250m,    250m };     // BTC-typical
        yield return new object[] { 30_000m, 250m,    100m };     // burst (>1.3×) → TP×0.8
        yield return new object[] { 30_000m, 70m,     100m };     // calm (<0.7×)  → TP×1.2
        yield return new object[] { 1.234m,  0.0050m, 0.0050m };  // alt-coin scale
    }

    [Theory]
    [MemberData(nameof(EntryAtrCases))]
    public void Long_brackets_position_sl_below_and_tp_above(decimal entry, decimal atr, decimal? atr50)
    {
        foreach (var s in new[] { StrategyType.Breakout, StrategyType.MeanReversion, StrategyType.Trend })
        {
            var b = BracketCalculator.Compute(entry, atr, BracketSide.Long, s, atr50);
            b.StopLoss.Should().BeLessThan(entry, $"long SL must sit below entry for {s}");
            b.TakeProfit.Should().BeGreaterThan(entry, $"long TP must sit above entry for {s}");
            b.RiskPerUnit.Should().BeGreaterThan(0m);
        }
    }

    [Theory]
    [MemberData(nameof(EntryAtrCases))]
    public void Short_brackets_position_sl_above_and_tp_below(decimal entry, decimal atr, decimal? atr50)
    {
        foreach (var s in new[] { StrategyType.Breakout, StrategyType.MeanReversion, StrategyType.Trend })
        {
            var b = BracketCalculator.Compute(entry, atr, BracketSide.Short, s, atr50);
            b.StopLoss.Should().BeGreaterThan(entry, $"short SL must sit above entry for {s}");
            b.TakeProfit.Should().BeLessThan(entry, $"short TP must sit below entry for {s}");
            b.RiskPerUnit.Should().BeGreaterThan(0m);
        }
    }

    [Theory]
    [InlineData(StrategyType.Breakout,      1.5,  3.0)]
    [InlineData(StrategyType.MeanReversion, 1.0,  1.5)]
    [InlineData(StrategyType.Trend,         2.0,  5.0)]
    public void Default_multipliers_match_section_43_table(StrategyType s, double slMult, double tpMult)
    {
        var (sl, tp) = BracketCalculator.DefaultMultipliers(s);
        sl.Should().Be((decimal)slMult);
        tp.Should().Be((decimal)tpMult);
    }

    [Fact]
    public void Volatility_high_burst_scales_tp_by_minus_20_percent()
    {
        // ratio = 200/100 = 2.0 > 1.3 ⇒ TP × 0.8
        var b = BracketCalculator.Compute(
            entry: 100m, atr14: 200m, side: BracketSide.Long,
            slMultiplier: 1.5m, tpMultiplier: 3.0m, atr50Sma: 100m);
        // SL untouched: 1.5 × 200 = 300
        b.StopLoss.Should().Be(100m - 300m);
        // TP scaled: 0.8 × 3.0 × 200 = 480
        b.TakeProfit.Should().Be(100m + 480m);
        b.EffectiveSlMultiplier.Should().Be(1.5m);
        b.EffectiveTpMultiplier.Should().Be(2.4m);
    }

    [Fact]
    public void Volatility_calm_extends_tp_by_plus_20_percent()
    {
        // ratio = 50/100 = 0.5 < 0.7 ⇒ TP × 1.2
        var b = BracketCalculator.Compute(
            entry: 100m, atr14: 50m, side: BracketSide.Long,
            slMultiplier: 1.5m, tpMultiplier: 3.0m, atr50Sma: 100m);
        b.EffectiveTpMultiplier.Should().Be(3.6m);
    }

    [Theory]
    [InlineData(0.7)]   // boundary low
    [InlineData(1.0)]   // identity
    [InlineData(1.3)]   // boundary high
    public void Volatility_at_boundaries_does_not_scale_tp(double ratio)
    {
        var atr = (decimal)ratio * 100m;
        var b = BracketCalculator.Compute(
            entry: 100m, atr14: atr, side: BracketSide.Long,
            slMultiplier: 1.5m, tpMultiplier: 3.0m, atr50Sma: 100m);
        b.EffectiveTpMultiplier.Should().Be(3.0m);
    }

    [Theory]
    [MemberData(nameof(EntryAtrCases))]
    public void Risk_per_unit_equals_abs_entry_minus_sl(decimal entry, decimal atr, decimal? atr50)
    {
        foreach (var side in new[] { BracketSide.Long, BracketSide.Short })
        {
            foreach (var s in new[] { StrategyType.Breakout, StrategyType.MeanReversion, StrategyType.Trend })
            {
                var b = BracketCalculator.Compute(entry, atr, side, s, atr50);
                b.RiskPerUnit.Should().Be(Math.Abs(entry - b.StopLoss));
            }
        }
    }

    [Theory]
    [InlineData(StrategyType.Breakout,      2.0)]
    [InlineData(StrategyType.MeanReversion, 1.5)]
    [InlineData(StrategyType.Trend,         2.5)]
    public void Risk_reward_ratio_in_neutral_volatility_matches_section_43(StrategyType s, double expectedRr)
    {
        var b = BracketCalculator.Compute(
            entry: 100m, atr14: 50m, side: BracketSide.Long, strategyType: s, atr50Sma: 50m);
        b.RiskReward(100m).Should().BeApproximately((decimal)expectedRr, 1e-6m);
    }

    [Fact]
    public void Throws_when_inputs_are_non_positive()
    {
        Action zeroEntry = () => BracketCalculator.Compute(
            entry: 0m, atr14: 50m, side: BracketSide.Long, strategyType: StrategyType.Breakout);
        zeroEntry.Should().Throw<ArgumentOutOfRangeException>();

        Action negSl = () => BracketCalculator.Compute(
            100m, 50m, BracketSide.Long, slMultiplier: -1m, tpMultiplier: 3m, atr50Sma: null);
        negSl.Should().Throw<ArgumentOutOfRangeException>();

        Action zeroTp = () => BracketCalculator.Compute(
            100m, 50m, BracketSide.Long, slMultiplier: 1.5m, tpMultiplier: 0m, atr50Sma: null);
        zeroTp.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Floors_zero_atr_to_a_tiny_positive_epsilon()
    {
        // ATR=0 would normally produce SL == entry. We clamp to a floor so the
        // bracket stays a non-degenerate rectangle.
        var b = BracketCalculator.Compute(
            entry: 100m, atr14: 0m, side: BracketSide.Long, strategyType: StrategyType.Breakout, atr50Sma: null);
        b.StopLoss.Should().BeLessThan(100m);
        b.TakeProfit.Should().BeGreaterThan(100m);
        b.RiskPerUnit.Should().BeGreaterThan(0m);
    }
}
