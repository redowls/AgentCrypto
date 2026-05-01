using FluentAssertions;
using TradingBot.Exchange.Resilience;
using Xunit;

namespace TradingBot.Tests.Exchange;

public sealed class BinanceApiExceptionTests
{
    [Theory]
    [InlineData(BinanceErrorCodes.Disconnected, 0, true, false)]
    [InlineData(BinanceErrorCodes.TooManyRequests, 0, true, false)]
    [InlineData(BinanceErrorCodes.RequestWeight, 0, true, false)]
    [InlineData(BinanceErrorCodes.InvalidTimestamp, 0, true, false)]
    [InlineData(0, 500, true, false)]
    [InlineData(0, 502, true, false)]
    [InlineData(0, 429, true, false)]
    [InlineData(0, 418, false, true)]
    [InlineData(-2010, 400, false, false)]
    public void IsRetryable_and_IsKillSwitch_match_spec(int code, int http, bool retryable, bool kill)
    {
        var ex = new BinanceApiException("test", code, http, "msg", null);
        ex.IsRetryable.Should().Be(retryable);
        ex.IsKillSwitch.Should().Be(kill);
    }

    [Fact]
    public void ParseRetryAfter_extracts_seconds_from_raw()
    {
        var ex = new BinanceApiException("test", 0, 429, "rate limit", "Retry-After: 30");
        ex.ParseRetryAfter().Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ParseRetryAfter_returns_null_when_absent()
    {
        var ex = new BinanceApiException("test", 0, 429, "rate limit", "no header here");
        ex.ParseRetryAfter().Should().BeNull();
    }
}
