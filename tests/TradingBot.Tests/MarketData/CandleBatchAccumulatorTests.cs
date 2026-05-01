using FluentAssertions;
using TradingBot.Core.Domain;
using TradingBot.MarketData.Persistence;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// The accumulator is the heart of the persistor's "≤ 500 rows OR ≤ 2s"
/// flush rule, so it gets exhaustive deterministic-clock coverage. Time is
/// purely an argument here, not <c>DateTime.UtcNow</c>, which lets these tests
/// run instantly without any sleeps.
/// </summary>
public sealed class CandleBatchAccumulatorTests
{
    private static readonly DateTime T0 = new(2026, 04, 27, 12, 00, 00, DateTimeKind.Utc);

    private static Candle Bar(int n) => new()
    {
        SymbolId = 1,
        Interval = "5m",
        OpenTime = T0.AddMinutes(5 * n),
        CloseTime = T0.AddMinutes(5 * n + 5),
        Open = 1m, High = 1m, Low = 1m, Close = 1m,
        Volume = 1m, QuoteVolume = 1m, TradeCount = 1, TakerBuyBase = 1m,
        IsClosed = true,
    };

    [Fact]
    public void Add_BelowMaxRows_DoesNotFlush()
    {
        var acc = new CandleBatchAccumulator(maxRows: 3, maxAge: TimeSpan.FromSeconds(2));

        var f1 = acc.Add(Bar(0), T0);
        var f2 = acc.Add(Bar(1), T0.AddMilliseconds(100));

        f1.Should().BeNull();
        f2.Should().BeNull();
        acc.Count.Should().Be(2);
        acc.FirstAddedUtc.Should().Be(T0);
    }

    [Fact]
    public void Add_AtMaxRows_FlushesAndResets()
    {
        var acc = new CandleBatchAccumulator(maxRows: 3, maxAge: TimeSpan.FromSeconds(2));

        acc.Add(Bar(0), T0);
        acc.Add(Bar(1), T0.AddMilliseconds(100));
        var flushed = acc.Add(Bar(2), T0.AddMilliseconds(200));

        flushed.Should().NotBeNull();
        flushed!.Should().HaveCount(3);
        acc.Count.Should().Be(0);
        acc.FirstAddedUtc.Should().BeNull();
    }

    [Fact]
    public void FlushIfDue_PartialBatch_FlushesWhenAgeExceeded()
    {
        var acc = new CandleBatchAccumulator(maxRows: 500, maxAge: TimeSpan.FromSeconds(2));
        acc.Add(Bar(0), T0);
        acc.Add(Bar(1), T0.AddMilliseconds(500));

        acc.FlushIfDue(T0.AddMilliseconds(1999)).Should().BeNull("we are still within the 2s window");

        var flushed = acc.FlushIfDue(T0.AddSeconds(2));
        flushed.Should().NotBeNull();
        flushed!.Should().HaveCount(2);
        acc.Count.Should().Be(0);
    }

    [Fact]
    public void FlushIfDue_EmptyBuffer_ReturnsNull()
    {
        var acc = new CandleBatchAccumulator(maxRows: 500, maxAge: TimeSpan.FromSeconds(2));
        acc.FlushIfDue(T0.AddHours(1)).Should().BeNull();
    }

    [Fact]
    public void NextFlushDeadline_IsFirstAddedPlusMaxAge()
    {
        var acc = new CandleBatchAccumulator(maxRows: 500, maxAge: TimeSpan.FromSeconds(2));
        acc.NextFlushDeadlineUtc.Should().BeNull();

        acc.Add(Bar(0), T0);
        acc.NextFlushDeadlineUtc.Should().Be(T0.AddSeconds(2));

        acc.Add(Bar(1), T0.AddMilliseconds(500));
        // Deadline must NOT shift to the second item — it's anchored to the
        // oldest buffered row, which is what bounds end-to-end latency.
        acc.NextFlushDeadlineUtc.Should().Be(T0.AddSeconds(2));
    }

    [Fact]
    public void FlushNow_DrainsRegardlessOfSizeOrAge()
    {
        var acc = new CandleBatchAccumulator(maxRows: 500, maxAge: TimeSpan.FromMinutes(5));
        acc.Add(Bar(0), T0);
        acc.Add(Bar(1), T0.AddMilliseconds(100));

        var drained = acc.FlushNow();

        drained.Should().NotBeNull().And.HaveCount(2);
        acc.Count.Should().Be(0);
        acc.FirstAddedUtc.Should().BeNull();
    }

    [Fact]
    public void Constructor_RejectsNonPositiveSizes()
    {
        Action negativeRows = () => new CandleBatchAccumulator(0, TimeSpan.FromSeconds(2));
        negativeRows.Should().Throw<ArgumentOutOfRangeException>();

        Action zeroAge = () => new CandleBatchAccumulator(500, TimeSpan.Zero);
        zeroAge.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_AfterDrain_ReanchorsDeadlineToNewBatch()
    {
        var acc = new CandleBatchAccumulator(maxRows: 2, maxAge: TimeSpan.FromSeconds(2));
        acc.Add(Bar(0), T0);
        acc.Add(Bar(1), T0.AddMilliseconds(100)); // triggers flush, resets state

        var t1 = T0.AddSeconds(10);
        acc.Add(Bar(2), t1);

        acc.FirstAddedUtc.Should().Be(t1);
        acc.NextFlushDeadlineUtc.Should().Be(t1.AddSeconds(2));
    }
}
