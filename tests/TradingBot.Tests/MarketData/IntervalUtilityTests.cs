using FluentAssertions;
using TradingBot.MarketData.Abstractions;
using Xunit;

namespace TradingBot.Tests.MarketData;

public sealed class IntervalUtilityTests
{
    [Theory]
    [InlineData("1m",  60)]
    [InlineData("5m",  300)]
    [InlineData("15m", 900)]
    [InlineData("1h",  3600)]
    [InlineData("4h",  14400)]
    [InlineData("1d",  86400)]
    public void ToTimeSpan_KnownIntervals(string code, int seconds)
    {
        IntervalUtility.ToTimeSpan(code).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void ToTimeSpan_UnknownInterval_Throws()
    {
        Action act = () => IntervalUtility.ToTimeSpan("17s");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FloorToInterval_BarBoundary()
    {
        var now = new DateTime(2026, 4, 27, 12, 47, 31, DateTimeKind.Utc);
        var floored = IntervalUtility.FloorToInterval(now, TimeSpan.FromMinutes(15));
        floored.Should().Be(new DateTime(2026, 4, 27, 12, 45, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void FloorToInterval_LocalTime_Throws()
    {
        var local = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Local);
        Action act = () => IntervalUtility.FloorToInterval(local, TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentException>();
    }
}
