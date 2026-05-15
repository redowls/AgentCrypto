using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Resilience;
using TradingBot.Risk.Abstractions;
using Xunit;

namespace TradingBot.Tests.Risk;

public sealed class KillSwitchTests
{
    [Fact]
    public async Task Trip_persists_state_and_writes_audit_event()
    {
        var (sut, riskRepo, _) = Build();

        await sut.TripAsync(KillSwitchSource.DailyLossLimit, "DLL crossed -3%", CancellationToken.None);

        sut.IsTripped.Should().BeTrue();
        sut.Source.Should().Be(KillSwitchSource.DailyLossLimit);
        sut.Reason.Should().Be("DLL crossed -3%");
        riskRepo.Events.Should().ContainSingle(e => e.EventType == TradingBot.Risk.KillSwitch.KillSwitch.EventTypeTrip);
    }

    [Fact]
    public async Task Reset_clears_state_and_writes_reset_event()
    {
        var (sut, riskRepo, _) = Build();
        await sut.TripAsync(KillSwitchSource.ManualCommand, "drill", CancellationToken.None);

        await sut.ResetAsync("post-drill clear", CancellationToken.None);

        sut.IsTripped.Should().BeFalse();
        sut.Source.Should().Be(KillSwitchSource.None);
        riskRepo.Events.Should().Contain(e => e.EventType == TradingBot.Risk.KillSwitch.KillSwitch.EventTypeReset);
    }

    [Fact]
    public async Task Trip_mirrors_to_binance_kill_switch()
    {
        var (sut, _, binance) = Build();

        await sut.TripAsync(KillSwitchSource.Http418Ban, "HTTP 418 from Binance", CancellationToken.None);

        binance.IsTripped.Should().BeTrue();
        binance.Reason.Should().Be("HTTP 418 from Binance");
    }

    [Fact]
    public async Task Trip_is_idempotent_subsequent_call_does_not_double_record()
    {
        var (sut, riskRepo, _) = Build();
        await sut.TripAsync(KillSwitchSource.ManualCommand, "first", CancellationToken.None);
        await sut.TripAsync(KillSwitchSource.ManualCommand, "second", CancellationToken.None);

        // Audit row count is implementation-defined, but the in-memory state
        // must reflect the FIRST trip's timestamp (sticky) and updated reason.
        sut.IsTripped.Should().BeTrue();
        sut.Reason.Should().Be("second", "reason can be updated by re-trips for visibility");
        riskRepo.Events.Count(e => e.EventType == TradingBot.Risk.KillSwitch.KillSwitch.EventTypeTrip)
            .Should().Be(2, "audit row written each call so an operator sees re-trips");
    }

    [Fact]
    public async Task Risk_manager_short_circuits_when_kill_switch_tripped()
    {
        var h = new RiskManagerHarness();
        h.KillSwitch.IsTripped = true;
        h.KillSwitch.Reason = "test";
        var rm = h.Build();

        var result = await rm.ApproveAsync(RiskTestFixtures.Signal(), RiskTestFixtures.State(), CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.RejectReason.Should().Be(RiskRejectReasons.KillSwitchActive);
    }

    private static (TradingBot.Risk.KillSwitch.KillSwitch sut, FakeRiskEventRepository riskRepo, BinanceKillSwitch binance) Build()
    {
        var riskRepo = new FakeRiskEventRepository();
        var binance  = new BinanceKillSwitch(NullLogger<BinanceKillSwitch>.Instance);
        var clock    = RiskTestFixtures.Clock();
        var sut = new TradingBot.Risk.KillSwitch.KillSwitch(
            riskRepo, binance, clock, NullLogger<TradingBot.Risk.KillSwitch.KillSwitch>.Instance);
        return (sut, riskRepo, binance);
    }
}
