using System.Diagnostics;
using FluentAssertions;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Indicators;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Performance contract from §5: 10,000 classifications must complete in &lt;1s.
/// This is a loose ceiling — the rule-based path is decimal arithmetic with no
/// allocations on the hot path apart from the result record + inputs dict — but
/// the test exists to catch a future regression where someone accidentally
/// adds an IO call or per-call LINQ pipeline.
/// </summary>
public sealed class RegimeClassifierPerformanceTests
{
    private readonly ITestOutputHelper _out;
    public RegimeClassifierPerformanceTests(ITestOutputHelper @out) => _out = @out;

    [Fact]
    public void Classify_10000_snapshots_under_one_second()
    {
        var classifier = new RegimeClassifier();
        var snaps = BuildVariedSnapshots(10_000);

        // Warmup — JITs the classifier and primes the hash bucket on the
        // inputs dict so the timed run isn't dominated by first-call costs.
        for (var i = 0; i < 100; i++)
        {
            _ = classifier.Classify(snaps[i % snaps.Length]);
        }

        var sw = Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < snaps.Length; i++)
        {
            var c = classifier.Classify(snaps[i]);
            if (c.Regime != Regime.Unknown) hits++;
        }
        sw.Stop();

        _out.WriteLine($"10k classifications: {sw.Elapsed.TotalMilliseconds:F1} ms, " +
                       $"{hits}/{snaps.Length} matched a rule");

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        // Sanity: at least some snapshots should match a rule — otherwise the
        // perf number is meaningless because the early-out path dominates.
        hits.Should().BeGreaterThan(snaps.Length / 4);
    }

    private static IndicatorSnapshot[] BuildVariedSnapshots(int count)
    {
        // Cycle through five archetype shapes so the perf run touches every
        // branch of the rule set, not just the early-Unknown path.
        var asOf = new DateTime(2026, 04, 29, 0, 0, 0, DateTimeKind.Utc);
        var arr = new IndicatorSnapshot[count];
        for (var i = 0; i < count; i++)
        {
            arr[i] = (i % 5) switch
            {
                0 => Trending(asOf, dirUp: true),
                1 => Trending(asOf, dirUp: false),
                2 => Ranging(asOf),
                3 => Volatile_(asOf),
                _ => Compressing(asOf),
            };
        }
        return arr;
    }

    private static IndicatorSnapshot Trending(DateTime asOf, bool dirUp) => new(
        AsOfUtc: asOf,
        Atr14: 100m, Ema9: 100m, Ema21: 100m, Ema50: 100m, Ema200: 100m,
        Adx14: 35m, PlusDi14: dirUp ? 30m : 12m, MinusDi14: dirUp ? 12m : 30m,
        Rsi14: 50m, BbUpper: 100m, BbMid: 100m, BbLower: 100m,
        BbWidth: 0.08m,
        DonchianUpper: 100m, DonchianLower: 100m, VwapSession: 100m,
        Atr50Sma: 100m, BbWidthSma50: 0.05m, BbWidthPercentileRank: 0.7m,
        BbWidthPrev: 0.05m, AdxPrev: 30m);

    private static IndicatorSnapshot Ranging(DateTime asOf) => new(
        AsOfUtc: asOf,
        Atr14: 100m, Ema9: 100m, Ema21: 100m, Ema50: 100m, Ema200: 100m,
        Adx14: 12m, PlusDi14: 18m, MinusDi14: 18m, Rsi14: 50m,
        BbUpper: 100m, BbMid: 100m, BbLower: 100m,
        BbWidth: 0.04m,
        DonchianUpper: 100m, DonchianLower: 100m, VwapSession: 100m,
        Atr50Sma: 100m, BbWidthSma50: 0.10m, BbWidthPercentileRank: 0.3m,
        BbWidthPrev: 0.04m, AdxPrev: 12m);

    private static IndicatorSnapshot Volatile_(DateTime asOf) => new(
        AsOfUtc: asOf,
        Atr14: 200m, Ema9: 100m, Ema21: 100m, Ema50: 100m, Ema200: 100m,
        Adx14: 22m, PlusDi14: 25m, MinusDi14: 22m, Rsi14: 50m,
        BbUpper: 100m, BbMid: 100m, BbLower: 100m, BbWidth: 0.05m,
        DonchianUpper: 100m, DonchianLower: 100m, VwapSession: 100m,
        Atr50Sma: 100m, BbWidthSma50: 0.05m, BbWidthPercentileRank: 0.5m,
        BbWidthPrev: 0.05m, AdxPrev: 22m);

    private static IndicatorSnapshot Compressing(DateTime asOf) => new(
        AsOfUtc: asOf,
        Atr14: 1m, Ema9: 100m, Ema21: 100m, Ema50: 100m, Ema200: 100m,
        Adx14: 18m, PlusDi14: 22m, MinusDi14: 18m, Rsi14: 50m,
        BbUpper: 100m, BbMid: 100m, BbLower: 100m, BbWidth: 0.01m,
        DonchianUpper: 100m, DonchianLower: 100m, VwapSession: 100m,
        Atr50Sma: 1m, BbWidthSma50: 0.05m, BbWidthPercentileRank: 0.05m,
        BbWidthPrev: 0.012m, AdxPrev: 14m);
}
