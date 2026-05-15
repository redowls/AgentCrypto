using FluentAssertions;
using TradingBot.Risk.Correlation;
using Xunit;

namespace TradingBot.Tests.Risk;

public sealed class CorrelationMathTests
{
    [Fact]
    public void Pearson_perfectly_correlated_series_returns_1()
    {
        var a = new[] { 1m, 2m, 3m, 4m, 5m };
        var b = new[] { 10m, 20m, 30m, 40m, 50m };
        CorrelationMath.Pearson(a, b).Should().BeApproximately(1m, 0.0001m);
    }

    [Fact]
    public void Pearson_perfectly_anti_correlated_returns_minus_1()
    {
        var a = new[] { 1m, 2m, 3m, 4m, 5m };
        var b = new[] { 5m, 4m, 3m, 2m, 1m };
        CorrelationMath.Pearson(a, b).Should().BeApproximately(-1m, 0.0001m);
    }

    [Fact]
    public void Pearson_constant_series_returns_zero()
    {
        var a = new[] { 1m, 1m, 1m, 1m };
        var b = new[] { 1m, 2m, 3m, 4m };
        CorrelationMath.Pearson(a, b).Should().Be(0m);
    }

    [Fact]
    public void Simple_returns_skips_first_bar_and_handles_zero_prev()
    {
        var closes = new[] { 100m, 110m, 99m };
        CorrelationMath.SimpleReturns(closes).Should().BeEquivalentTo(new[] { 0.10m, -0.10m });
    }

    [Fact]
    public void Greedy_clusters_groups_high_corr_pairs_into_same_label()
    {
        // 4 symbols. Pairs (0,1)=0.9 and (2,3)=0.85 cross threshold 0.7.
        // (0,2) = 0.3 → no link.
        var pairs = new (int, int, decimal)[]
        {
            (0, 1, 0.90m),
            (0, 2, 0.30m),
            (0, 3, 0.20m),
            (1, 2, 0.25m),
            (1, 3, 0.10m),
            (2, 3, 0.85m),
        };
        var labels = CorrelationMath.AssignClusters(4, pairs, 0.70m);

        labels[0].Should().Be(labels[1], "0 and 1 share the same cluster (corr 0.9 > 0.7)");
        labels[2].Should().Be(labels[3], "2 and 3 share the same cluster (corr 0.85 > 0.7)");
        labels[0].Should().NotBe(labels[2], "0 and 2 are uncorrelated");
    }

    [Fact]
    public void Greedy_chains_via_transitivity_under_single_link()
    {
        // 0-1 and 1-2 strong; 0-2 itself weak. Single-link merges them all.
        var pairs = new (int, int, decimal)[]
        {
            (0, 1, 0.90m),
            (1, 2, 0.85m),
            (0, 2, 0.20m),
        };
        var labels = CorrelationMath.AssignClusters(3, pairs, 0.70m);
        labels[0].Should().Be(labels[1]);
        labels[1].Should().Be(labels[2]);
    }
}
