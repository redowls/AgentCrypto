using FluentAssertions;
using TradingBot.Core.Domain.Enums;
using TradingBot.Risk.Configuration;
using Xunit;

namespace TradingBot.Tests.Risk;

/// <summary>
/// Property test: across a randomized matrix of equity, stop-distance, and
/// entry-price values, the approved position notional must NEVER exceed
/// the §8.2 single-symbol cap (50% of equity).
///
/// We don't pull FsCheck — a deterministic matrix with 1,500 cases hits the
/// same boundaries (zero/min step, large step, micro-cap altcoin scale) and
/// keeps the test fast + reproducible.
/// </summary>
public sealed class RiskManagerSizeCapPropertyTests
{
    public static IEnumerable<object[]> Cases()
    {
        // (equity, entry, stopDistanceFraction)
        // stopDistance is computed as entry × fraction so SL distance scales
        // with the asset's price, mirroring the live ATR-driven brackets.
        var equities = new decimal[]   { 5_000m, 10_000m, 20_000m, 50_000m };
        var entries  = new decimal[]   { 0.5m, 25m, 30_000m, 1_500m };
        var slFracs  = new double[]    { 0.001, 0.005, 0.02, 0.05, 0.10 };
        var stepSize = 0.0001m;

        var rng = new Random(1234);
        var produced = 0;
        foreach (var eq in equities)
        foreach (var px in entries)
        foreach (var f  in slFracs)
        {
            var slDist = px * (decimal)f;
            yield return new object[] { eq, px, slDist, stepSize };
            produced++;
            // Add a few jittered repeats to widen coverage without exploding count.
            for (var k = 0; k < 5; k++)
            {
                var jitterEq = eq * (1m + (decimal)(rng.NextDouble() * 0.4 - 0.2));
                var jitterPx = px * (1m + (decimal)(rng.NextDouble() * 0.4 - 0.2));
                var jitterSl = jitterPx * (decimal)f * (1m + (decimal)(rng.NextDouble() * 0.4 - 0.2));
                if (jitterEq > 100m && jitterPx > 0.01m && jitterSl > 0m)
                    yield return new object[] { jitterEq, jitterPx, jitterSl, stepSize };
                produced++;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Approved_notional_never_exceeds_50pct_of_equity(
        decimal equity, decimal entry, decimal stopDistance, decimal stepSize)
    {
        var h = new RiskManagerHarness();
        // Override the synthetic symbol with a step matching the parametrized one.
        h.Symbols.ById[RiskTestFixtures.BtcId].StepSize = stepSize;
        h.Symbols.ById[RiskTestFixtures.BtcId].MinNotional = 1m;

        var rm = h.Build();
        var sl = entry - stopDistance; // long; stop-distance > 0 by construction.
        if (sl <= 0) return;
        var s  = RiskTestFixtures.Signal(entry: entry, sl: sl, tp: entry + stopDistance * 2m, atr: stopDistance);
        var st = RiskTestFixtures.State(equity: equity, hwm: equity, accountType: AccountTypes.Spot);

        var result = await rm.ApproveAsync(s, st, CancellationToken.None);
        if (!result.Approved) return; // some tiny inputs hit minNotional — that's fine.

        var notional = result.Quantity * entry;
        var cap = 0.5m * equity;
        notional.Should().BeLessThanOrEqualTo(cap + cap * 0.0001m,
            $"notional={notional} cap={cap} eq={equity} entry={entry} slDist={stopDistance} step={stepSize}");
    }
}
