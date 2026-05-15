using FluentAssertions;
using Microsoft.Extensions.Options;
using TradingBot.AI.Configuration;
using TradingBot.AI.Cost;
using Xunit;

namespace TradingBot.Tests.AI;

public sealed class DailyCostMeterTests
{
    private static DailyCostMeter Build(decimal cap, FakeClock clock)
        => new(Options.Create(new ClaudeOptions { DailyCapUsd = cap }), clock);

    [Fact]
    public void TryReserve_returns_true_until_cap_reached_then_false()
    {
        var clock = new FakeClock(new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc));
        var m = Build(cap: 0.10m, clock);

        m.TryReserve(out _).Should().BeTrue();
        m.Record(0.05m);
        m.TryReserve(out var rem).Should().BeTrue();
        rem.Should().Be(0.05m);

        m.Record(0.06m);
        m.TryReserve(out _).Should().BeFalse();
    }

    [Fact]
    public void Rolls_over_to_zero_at_next_utc_day()
    {
        var clock = new FakeClock(new DateTime(2026, 5, 8, 23, 0, 0, DateTimeKind.Utc));
        var m = Build(cap: 1m, clock);
        m.Record(0.99m);
        m.SpentTodayUsd.Should().Be(0.99m);

        clock.Advance(TimeSpan.FromHours(2));   // Crosses 00:00 UTC.
        m.SpentTodayUsd.Should().Be(0m);
        m.TryReserve(out _).Should().BeTrue();
    }
}
