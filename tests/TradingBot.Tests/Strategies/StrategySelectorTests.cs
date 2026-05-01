using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Selection;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Verifies the §3.4 regime → strategy mapping with all three concrete
/// strategies registered. We use lightweight stubs so the test isn't entangled
/// with the indicator math.
/// </summary>
public sealed class StrategySelectorTests
{
    private static StrategySelector BuildSelector()
    {
        var strategies = new IStrategy[]
        {
            new StubStrategy(StrategyType.Breakout,      StrategyCodes.BreakoutDonchian,    "1h"),
            new StubStrategy(StrategyType.MeanReversion, StrategyCodes.MeanReversionBbVwap, "15m"),
            new StubStrategy(StrategyType.Trend,         StrategyCodes.TrendEmaAdx,         "1h", htf: "4h"),
        };
        return new StrategySelector(strategies, NullLogger<StrategySelector>.Instance);
    }

    [Fact]
    public void Trending_up_returns_trend_primary_then_breakout_full_size()
    {
        var sel = BuildSelector();

        var assignments = sel.GetActive(Regime.TrendingUp);

        assignments.Should().HaveCount(2);
        assignments[0].Strategy.StrategyType.Should().Be(StrategyType.Trend);
        assignments[0].SizeMultiplier.Should().Be(1.0m);
        assignments[1].Strategy.StrategyType.Should().Be(StrategyType.Breakout);
        assignments[1].SizeMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public void Trending_down_returns_trend_then_breakout_full_size()
    {
        var assignments = BuildSelector().GetActive(Regime.TrendingDown);

        assignments.Select(a => a.Strategy.StrategyType)
            .Should().ContainInOrder(StrategyType.Trend, StrategyType.Breakout);
        assignments.Should().AllSatisfy(a => a.SizeMultiplier.Should().Be(1.0m));
    }

    [Fact]
    public void Ranging_returns_only_mean_reversion()
    {
        var assignments = BuildSelector().GetActive(Regime.Ranging);

        assignments.Should().HaveCount(1);
        assignments[0].Strategy.StrategyType.Should().Be(StrategyType.MeanReversion);
        assignments[0].SizeMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public void Volatile_returns_breakout_at_half_size()
    {
        var assignments = BuildSelector().GetActive(Regime.Volatile);

        assignments.Should().HaveCount(1);
        assignments[0].Strategy.StrategyType.Should().Be(StrategyType.Breakout);
        assignments[0].SizeMultiplier.Should().Be(0.5m);
    }

    [Fact]
    public void Compressing_returns_no_strategies()
    {
        BuildSelector().GetActive(Regime.Compressing).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_returns_no_strategies()
    {
        BuildSelector().GetActive(Regime.Unknown).Should().BeEmpty();
    }

    [Fact]
    public void Missing_strategy_registration_just_omits_it_from_the_route()
    {
        // Only Breakout is registered.
        var only = new IStrategy[]
        {
            new StubStrategy(StrategyType.Breakout, StrategyCodes.BreakoutDonchian, "1h"),
        };
        var sel = new StrategySelector(only, NullLogger<StrategySelector>.Instance);

        // TrendingUp wants (Trend, Breakout) — only Breakout is available.
        sel.GetActive(Regime.TrendingUp).Should().ContainSingle()
            .Which.Strategy.StrategyType.Should().Be(StrategyType.Breakout);

        // Ranging wants MeanReversion — none available.
        sel.GetActive(Regime.Ranging).Should().BeEmpty();
    }

    private sealed class StubStrategy(StrategyType type, string code, string tf, string? htf = null) : IStrategy
    {
        public string Name => code;
        public string PrimaryTimeframe => tf;
        public string? HigherTimeframe => htf;
        public StrategyType StrategyType => type;
        public Regime[] AllowedRegimes => Array.Empty<Regime>();
        public SignalCandidate? Evaluate(IndicatorSnapshot snap, IndicatorSnapshot? htf, Regime regime, MarketContext ctx) => null;
    }
}
