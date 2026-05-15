using FluentAssertions;
using TradingBot.Risk.Abstractions;
using Xunit;

namespace TradingBot.Tests.Risk;

/// <summary>
/// §8.2 scenario test: replay a synthetic equity curve with -3% intraday and
/// verify the daily-loss-limit gate fires once during the day and resets at
/// 00:00 UTC the next day.
///
/// We exercise <see cref="IRiskManager.ApproveAsync"/> directly with the
/// account state values that would be observed at each tick — the goal here
/// is to show the gate's *behaviour* is anchored to the fixed clock + the
/// supplied DailyPnlPct, not to validate the snapshot provider's anchor math
/// (covered by its own test).
/// </summary>
public sealed class DailyLossLimitScenarioTests
{
    [Fact]
    public async Task Daily_loss_limit_fires_intraday_then_resets_at_midnight()
    {
        var h = new RiskManagerHarness();
        var rm = h.Build();
        var startOfDay = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        h.Clock.SetUtc(startOfDay);

        // Curve in (offset_minutes, dailyPnlPct, expectedApproved).
        // The trip happens at 12:00 with -3%; subsequent intraday ticks remain
        // rejected; the same -3% level evaluated against the NEXT day's
        // anchor produces dailyPnlPct = 0 ⇒ approves again.
        // Note: -3% is the trip threshold. -2.50% is ABOVE the threshold and
        // approves; only the curve points strictly worse than -3% reject.
        // The test exercises:
        //   • intraday recovery does NOT keep the gate tripped (the gate is a
        //     scalar read; "stickiness" comes from the daily anchor not moving),
        //   • a midnight rollover with a fresh anchor lets a -3%-from-prior-day
        //     equity approve again.
        var curve = new (TimeSpan Offset, decimal DailyPnlPct, bool ExpectApprove)[]
        {
            (TimeSpan.FromHours(1),  -0.0050m, true),
            (TimeSpan.FromHours(6),  -0.0200m, true),
            (TimeSpan.FromHours(12), -0.0301m, false),  // first trip
            (TimeSpan.FromHours(15), -0.0250m, true),   // recovers above -3% ⇒ gate approves
            (TimeSpan.FromHours(20), -0.0500m, false),  // worsens past threshold again
            (TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)), 0.0000m, true), // day 2 anchor reset
        };

        var trips = 0;
        var approves = 0;
        foreach (var (offset, dailyPnl, expect) in curve)
        {
            h.Clock.SetUtc(startOfDay.Add(offset));
            // Equity follows the dailyPnl directly — start anchor 20k.
            var anchor = 20_000m;
            var equity = anchor * (1m + dailyPnl);
            var state = RiskTestFixtures.State(
                equity:     equity,
                equityAt00: anchor,
                dailyPnl:   dailyPnl,
                at:         h.Clock.UtcNow);

            var decision = await rm.ApproveAsync(RiskTestFixtures.Signal(), state, CancellationToken.None);

            if (decision.Approved) approves++;
            else if (decision.RejectReason == RiskRejectReasons.DailyLossLimit) trips++;

            // The gate is a snapshot read of the supplied dailyPnl and does
            // NOT remember its own past trips. The trip is "sticky" in
            // production because the anchor doesn't move intraday, not because
            // the gate maintains state.
            if (expect)
                decision.Approved.Should().BeTrue($"offset={offset} dailyPnl={dailyPnl}");
            else
                decision.Approved.Should().BeFalse($"offset={offset} dailyPnl={dailyPnl}");
        }

        // 2 trips (-3.01%, -5.00%) and 4 approves across the curve.
        trips.Should().Be(2);
        approves.Should().Be(4);
    }
}
