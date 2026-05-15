using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using TradingBot.AI.Configuration;
using TradingBot.AI.Cost;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task Burst_within_capacity_does_not_wait()
    {
        var clock = new FakeClock(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        var lim = new TokenBucketRateLimiter(
            Options.Create(new ClaudeOptions { RequestsPerMinute = 10 }), clock);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) await lim.WaitAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task After_exhaustion_advancing_clock_refills_tokens()
    {
        var clock = new FakeClock(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        var lim = new TokenBucketRateLimiter(
            Options.Create(new ClaudeOptions { RequestsPerMinute = 60 }), clock);

        // Drain the bucket.
        for (var i = 0; i < 60; i++) await lim.WaitAsync(CancellationToken.None);

        // 60 RPM = 1 token/sec. Advance 5 simulated seconds and 5 more
        // Wait calls should succeed without real-world delay.
        clock.Advance(TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await lim.WaitAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50),
            "5 tokens should have refilled by 5 seconds of simulated time");
    }
}
