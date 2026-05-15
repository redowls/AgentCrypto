using FluentAssertions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Execution.Trailing;
using Xunit;

namespace TradingBot.Tests.Execution;

public class TrailingStopRulesTests
{
    [Fact]
    public void Long_trail_stops_below_close()
    {
        var sl = TrailingStopRules.ComputeTrailedStop(closePrice: 110m, atr14: 2m, trailMultiplier: 1.5m, PositionSides.Long);
        sl.Should().Be(110m - 1.5m * 2m);
    }

    [Fact]
    public void Short_trail_stops_above_close()
    {
        var sl = TrailingStopRules.ComputeTrailedStop(closePrice: 90m, atr14: 2m, trailMultiplier: 1.5m, PositionSides.Short);
        sl.Should().Be(90m + 1.5m * 2m);
    }

    [Fact]
    public void Long_replace_only_when_new_stop_raises_floor()
    {
        TrailingStopRules.ShouldReplace(currentStop: 100m, candidateStop: 105m, PositionSides.Long).Should().BeTrue();
        TrailingStopRules.ShouldReplace(currentStop: 100m, candidateStop:  95m, PositionSides.Long).Should().BeFalse();
        TrailingStopRules.ShouldReplace(currentStop: 100m, candidateStop: 100m, PositionSides.Long).Should().BeFalse();
    }

    [Fact]
    public void Short_replace_only_when_new_stop_lowers_ceiling()
    {
        TrailingStopRules.ShouldReplace(currentStop: 100m, candidateStop:  95m, PositionSides.Short).Should().BeTrue();
        TrailingStopRules.ShouldReplace(currentStop: 100m, candidateStop: 105m, PositionSides.Short).Should().BeFalse();
    }

    [Fact]
    public void Partial_take_fires_when_long_reaches_2R()
    {
        var pos = new Position
        {
            Side          = PositionSides.Long,
            AvgEntryPrice = 100m,
            StopLoss      = 90m, // R = 10
            Quantity      = 1m,
        };
        TrailingStopRules.ShouldPartialTake(pos, currentPrice: 119m, rMultiple: 2m).Should().BeFalse();
        TrailingStopRules.ShouldPartialTake(pos, currentPrice: 120m, rMultiple: 2m).Should().BeTrue();
        TrailingStopRules.ShouldPartialTake(pos, currentPrice: 200m, rMultiple: 2m).Should().BeTrue();
    }

    [Fact]
    public void Partial_take_fires_when_short_reaches_2R()
    {
        var pos = new Position
        {
            Side          = PositionSides.Short,
            AvgEntryPrice = 100m,
            StopLoss      = 110m, // R = 10
            Quantity      = 1m,
        };
        TrailingStopRules.ShouldPartialTake(pos, currentPrice: 81m, rMultiple: 2m).Should().BeFalse();
        TrailingStopRules.ShouldPartialTake(pos, currentPrice: 80m, rMultiple: 2m).Should().BeTrue();
    }

    [Theory]
    [InlineData(3,  4, false)]   // not enough bars yet
    [InlineData(4,  4, true)]    // bars elapsed AND no +1R progress
    [InlineData(20, 4, true)]    // long past
    public void Time_exit_fires_when_bars_elapsed_without_R_progress(int held, int max, bool expected)
    {
        var pos = new Position
        {
            Side = PositionSides.Long,
            AvgEntryPrice = 100m,
            StopLoss = 90m,
        };
        TrailingStopRules.ShouldTimeExit(pos, currentPrice: 105m, barsHeld: held, maxBars: max)
            .Should().Be(expected);
    }

    [Fact]
    public void Time_exit_does_not_fire_when_position_is_in_profit_beyond_R()
    {
        var pos = new Position { Side = PositionSides.Long, AvgEntryPrice = 100m, StopLoss = 90m };
        TrailingStopRules.ShouldTimeExit(pos, currentPrice: 115m /* +1.5R */, barsHeld: 100, maxBars: 4)
            .Should().BeFalse();
    }

    [Fact]
    public void DefaultsFor_returns_strategy_specific_multipliers()
    {
        TrailingStopRules.DefaultsFor("BREAKOUT_DON").TrailMult.Should().Be(1.5m);
        TrailingStopRules.DefaultsFor("MR_BB_VWAP").TrailMult.Should().Be(1.0m);
        TrailingStopRules.DefaultsFor("TREND_EMA_ADX").TrailMult.Should().Be(2.0m);
        TrailingStopRules.DefaultsFor("UNKNOWN").Should().Be((1.5m, CandleIntervals.OneHour));
    }
}
