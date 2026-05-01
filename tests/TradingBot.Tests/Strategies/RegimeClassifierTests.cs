using FluentAssertions;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Indicators;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Synthetic-snapshot fixtures targeting each branch of the §3.4 rules. We
/// deliberately bypass <c>IndicatorComputer</c> here — the classifier is a pure
/// function of the snapshot, so feeding it crafted snapshots yields stronger,
/// less noisy assertions than driving a long quote series through Skender.
///
/// Indicator-correctness for the snapshot itself is covered by
/// <c>IndicatorComputerTests</c>; here we trust those numbers and focus on the
/// classifier's decision boundaries.
/// </summary>
public sealed class RegimeClassifierTests
{
    private static readonly DateTime AsOf = new(2026, 04, 29, 12, 0, 0, DateTimeKind.Utc);

    private static IndicatorSnapshot Base(
        decimal? adx           = 22m,
        decimal? adxPrev       = 22m,
        decimal? plusDi        = 25m,
        decimal? minusDi       = 22m,
        decimal? atr           = 100m,
        decimal? atr50Sma      = 100m,
        decimal? bbWidth       = 0.05m,
        decimal? bbWidthPrev   = 0.05m,
        decimal? bbWidthSma50  = 0.05m,
        decimal? bbWidthPctRank = 0.5m,
        decimal? ema21         = 100m,
        decimal? ema50         = 100m)
        => new(
            AsOfUtc: AsOf,
            Atr14: atr,
            Ema9: 100m, Ema21: ema21, Ema50: ema50, Ema200: 100m,
            Adx14: adx, PlusDi14: plusDi, MinusDi14: minusDi,
            Rsi14: 50m,
            BbUpper: 100m, BbMid: 100m, BbLower: 100m,
            BbWidth: bbWidth,
            DonchianUpper: 100m, DonchianLower: 100m,
            VwapSession: 100m,
            Atr50Sma: atr50Sma,
            BbWidthSma50: bbWidthSma50,
            BbWidthPercentileRank: bbWidthPctRank,
            BbWidthPrev: bbWidthPrev,
            AdxPrev: adxPrev);

    private readonly RegimeClassifier _classifier = new();

    [Fact]
    public void Insufficient_inputs_classify_as_unknown()
    {
        var snap = Base(adx: null);
        var c = _classifier.Classify(snap);
        c.Regime.Should().Be(Regime.Unknown);
        c.Confidence.Should().Be(0m);
    }

    [Fact]
    public void Trending_up_when_adx_high_bbw_expanding_and_pdi_dominant()
    {
        var snap = Base(
            adx: 35m, adxPrev: 30m,
            plusDi: 30m, minusDi: 15m,
            bbWidth: 0.08m, bbWidthPrev: 0.05m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().Be(Regime.TrendingUp);
        c.Confidence.Should().BeGreaterThan(0.5m);
    }

    [Fact]
    public void Trending_down_when_adx_high_bbw_expanding_and_mdi_dominant()
    {
        var snap = Base(
            adx: 35m, adxPrev: 30m,
            plusDi: 12m, minusDi: 30m,
            bbWidth: 0.08m, bbWidthPrev: 0.05m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().Be(Regime.TrendingDown);
    }

    [Fact]
    public void Trending_does_not_match_when_bbw_is_contracting()
    {
        var snap = Base(
            adx: 35m,
            plusDi: 30m, minusDi: 15m,
            bbWidth: 0.04m, bbWidthPrev: 0.05m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().NotBe(Regime.TrendingUp);
        c.Regime.Should().NotBe(Regime.TrendingDown);
    }

    [Fact]
    public void Ranging_when_adx_below_20_and_bbw_below_70pct_of_sma50()
    {
        var snap = Base(
            adx: 12m,
            // 0.04 < 0.7 × 0.10 = 0.07 ✓
            bbWidth: 0.04m, bbWidthSma50: 0.10m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().Be(Regime.Ranging);
        c.Confidence.Should().BeGreaterThan(0.5m);
    }

    [Fact]
    public void Ranging_does_not_match_when_bbw_above_70pct_of_sma()
    {
        var snap = Base(
            adx: 12m,
            // 0.08 > 0.7 × 0.10 = 0.07 ✗
            bbWidth: 0.08m, bbWidthSma50: 0.10m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().NotBe(Regime.Ranging);
    }

    [Fact]
    public void Volatile_when_atr_burst_and_adx_in_2025_band()
    {
        var snap = Base(
            adx: 22m,
            // 200 > 1.5 × 100 = 150 ✓
            atr: 200m, atr50Sma: 100m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().Be(Regime.Volatile);
        c.Confidence.Should().BeGreaterThan(0.5m);
    }

    [Fact]
    public void Volatile_does_not_match_when_adx_outside_2025_band()
    {
        var snap = Base(
            adx: 30m, // outside [20, 25]
            atr: 200m, atr50Sma: 100m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().NotBe(Regime.Volatile);
    }

    [Fact]
    public void Compressing_when_bbw_pct_rank_low_and_adx_rising()
    {
        var snap = Base(
            adx: 18m, adxPrev: 14m,
            bbWidth: 0.02m, bbWidthSma50: 0.10m,
            bbWidthPctRank: 0.05m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().Be(Regime.Compressing);
        c.Confidence.Should().BeGreaterThan(0.5m);
    }

    [Fact]
    public void Compressing_does_not_match_when_adx_falling()
    {
        var snap = Base(
            adx: 14m, adxPrev: 18m,
            bbWidthPctRank: 0.05m);

        var c = _classifier.Classify(snap);

        c.Regime.Should().NotBe(Regime.Compressing);
    }

    [Fact]
    public void Confidence_is_clamped_to_unit_interval()
    {
        // Extreme inputs that would otherwise blow past 1.0 in the scoring math.
        var snap = Base(
            adx: 80m, adxPrev: 30m,
            plusDi: 50m, minusDi: 5m,
            bbWidth: 0.50m, bbWidthPrev: 0.05m);

        var c = _classifier.Classify(snap);
        c.Confidence.Should().BeInRange(0m, 1m);
    }

    [Fact]
    public void Inputs_dictionary_includes_drivers_for_audit()
    {
        var snap = Base();
        var c = _classifier.Classify(snap);

        c.Inputs.Should().ContainKey("adx14");
        c.Inputs.Should().ContainKey("bbWidth");
        c.Inputs.Should().ContainKey("atr14");
        c.Inputs.Should().ContainKey("dirUp");
    }

    [Fact]
    public async Task ClassifyAsync_returns_same_result_as_sync()
    {
        var snap = Base(
            adx: 35m, adxPrev: 30m,
            plusDi: 30m, minusDi: 15m,
            bbWidth: 0.08m, bbWidthPrev: 0.05m);

        var sync  = _classifier.Classify(snap);
        var async_ = await _classifier.ClassifyAsync(snap, CancellationToken.None);

        async_.Regime.Should().Be(sync.Regime);
        async_.Confidence.Should().Be(sync.Confidence);
    }
}
