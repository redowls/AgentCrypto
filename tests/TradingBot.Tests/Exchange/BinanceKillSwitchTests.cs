using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Exchange.Resilience;
using Xunit;

namespace TradingBot.Tests.Exchange;

public sealed class BinanceKillSwitchTests
{
    [Fact]
    public void Trip_records_reason_and_retryAfter()
    {
        var ks = new BinanceKillSwitch(NullLogger<BinanceKillSwitch>.Instance);
        var retryAfter = DateTime.UtcNow.AddMinutes(5);

        ks.IsTripped.Should().BeFalse();

        ks.Trip("HTTP 418", retryAfter);

        ks.IsTripped.Should().BeTrue();
        ks.Reason.Should().Be("HTTP 418");
        ks.RetryAfterUtc.Should().Be(retryAfter);
        ks.TrippedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Reset_clears_state()
    {
        var ks = new BinanceKillSwitch(NullLogger<BinanceKillSwitch>.Instance);
        ks.Trip("test", null);
        ks.Reset();

        ks.IsTripped.Should().BeFalse();
        ks.Reason.Should().BeNull();
    }

    [Fact]
    public void Trip_is_idempotent_first_call_wins()
    {
        var ks = new BinanceKillSwitch(NullLogger<BinanceKillSwitch>.Instance);
        ks.Trip("first", null);
        ks.Trip("second", null);

        ks.Reason.Should().Be("first");
    }
}
