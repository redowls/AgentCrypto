using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Strategies;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// §3.1 BREAKOUT_DON golden tests. Hand-crafted snapshots target each gate so
/// a regression in one rule (e.g., volume multiplier flipped to 1.0×) shows
/// up immediately as a single failing test naming the gate.
/// </summary>
public sealed class BreakoutDonchianStrategyTests
{
    private static BreakoutDonchianStrategy Build(BreakoutDonchianOptions? opts = null)
    {
        var monitor = new StaticOptionsMonitor<BreakoutDonchianOptions>(opts ?? new BreakoutDonchianOptions());
        return new BreakoutDonchianStrategy(monitor, NullLogger<BreakoutDonchianStrategy>.Instance);
    }

    [Fact]
    public void Long_breakout_hits_when_all_gates_pass()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 100m, ema200: 9_500m, adx: 25m,
            donchianUpper: 11_000m, donchianLower: 9_000m);

        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 11_005m, barHigh: 11_010m, barVolume: 200m, volumeSma20: 100m);

        var strat = Build();

        var result = strat.Evaluate(snap, null, Regime.TrendingUp, ctx);

        result.Should().NotBeNull();
        result!.StrategyCode.Should().Be(StrategyCodes.BreakoutDonchian);
        result.Side.Should().Be(Sides.Buy);
        result.EntryPrice.Should().Be(11_005m);
        // §4.3 default for Breakout: SL = entry − 1.5×ATR, TP = entry + 3×ATR
        result.StopLoss.Should().Be(11_005m - 1.5m * 100m);
        // Volatility adjustment: atr14/atr50Sma = 1.0 → no scale
        result.TakeProfit.Should().Be(11_005m + 3.0m * 100m);
        result.AtrValue.Should().Be(100m);
        result.Trail.Should().NotBeNull();
        result.Trail!.Mode.Should().Be(TrailMode.Chandelier);
    }

    [Fact]
    public void Long_breakout_misses_when_close_does_not_break_donchian()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(donchianUpper: 11_000m, ema200: 9_500m, adx: 25m);
        // Close strictly below the band → no break
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 10_990m, barHigh: 10_995m);

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_breakout_misses_when_volume_below_threshold()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(donchianUpper: 11_000m, ema200: 9_500m, adx: 25m);
        // Volume below 1.5× SMA20: 100 < 1.5×100 = 150
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 11_005m, barHigh: 11_010m, barVolume: 100m, volumeSma20: 100m);

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_breakout_misses_when_close_below_ema200()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            donchianUpper: 11_000m, ema200: 11_500m, adx: 25m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 11_005m, barHigh: 11_010m);

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Long_breakout_misses_when_adx_below_threshold()
    {
        // adx 18 < threshold 20 (default)
        var snap = StrategyFixtures.SnapshotAllPopulated(donchianUpper: 11_000m, ema200: 9_500m, adx: 18m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 11_005m, barHigh: 11_010m);

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }

    [Fact]
    public void Disabled_options_short_circuits_the_strategy()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(donchianUpper: 11_000m, ema200: 9_500m, adx: 25m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 11_005m, barHigh: 11_010m);

        Build(new BreakoutDonchianOptions { Enabled = false })
            .Evaluate(snap, null, Regime.TrendingUp, ctx)
            .Should().BeNull();
    }

    [Fact]
    public void Short_breakout_hits_when_close_below_lower_band_in_trendingdown()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(
            donchianUpper: 11_000m, donchianLower: 9_000m, ema200: 10_500m, adx: 25m);
        // Close below the lower band, EMA200 above (downtrend), volume confirms.
        var ctx = StrategyFixtures.CtxAllPopulated(
            barClose: 8_995m, barLow: 8_990m, barHigh: 9_010m, barVolume: 200m, volumeSma20: 100m);

        var result = Build().Evaluate(snap, null, Regime.TrendingDown, ctx);

        result.Should().NotBeNull();
        result!.Side.Should().Be(Sides.Sell);
        // SL above for shorts
        result.StopLoss.Should().Be(8_995m + 1.5m * 100m);
        result.TakeProfit.Should().Be(8_995m - 3.0m * 100m);
    }

    [Fact]
    public void Volatility_high_burst_reduces_take_profit_by_20_percent()
    {
        // atr14/atr50Sma = 1.5 > 1.3 → TP × 0.8
        var snap = StrategyFixtures.SnapshotAllPopulated(
            atr: 150m, atr50Sma: 100m,
            donchianUpper: 11_000m, ema200: 9_500m, adx: 25m);
        var ctx = StrategyFixtures.CtxAllPopulated(barClose: 11_005m, barHigh: 11_010m);

        var result = Build().Evaluate(snap, null, Regime.TrendingUp, ctx);

        result.Should().NotBeNull();
        // Effective TP multiplier = 3.0 × 0.8 = 2.4 → TP = entry + 2.4 × 150 = entry + 360
        result!.TakeProfit.Should().Be(11_005m + 2.4m * 150m);
    }

    /// <summary>Snapshot missing critical fields ⇒ warm-up not done; null result.</summary>
    [Fact]
    public void Returns_null_when_snapshot_is_warming_up()
    {
        var snap = StrategyFixtures.SnapshotAllPopulated(donchianUpper: null);
        var ctx = StrategyFixtures.CtxAllPopulated();

        Build().Evaluate(snap, null, Regime.TrendingUp, ctx).Should().BeNull();
    }
}

/// <summary>Test double for IOptionsMonitor that always returns a fixed value
/// and never raises a change notification — sufficient for unit tests.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
