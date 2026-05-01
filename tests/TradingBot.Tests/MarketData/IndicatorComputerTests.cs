using FluentAssertions;
using Skender.Stock.Indicators;
using TradingBot.MarketData.Indicators;
using Xunit;

namespace TradingBot.Tests.MarketData;

public sealed class IndicatorComputerTests
{
    [Fact]
    public void Compute_EmptySeries_ReturnsAllNullIndicators()
    {
        var snap = IndicatorComputer.Compute(Array.Empty<Quote>(),
            sessionStartUtc: DateTime.UtcNow.Date);

        snap.Atr14.Should().BeNull();
        snap.Ema9.Should().BeNull();
        snap.Ema200.Should().BeNull();
        snap.Adx14.Should().BeNull();
        snap.Rsi14.Should().BeNull();
        snap.BbUpper.Should().BeNull();
        snap.DonchianUpper.Should().BeNull();
        snap.VwapSession.Should().BeNull();
    }

    [Fact]
    public void Compute_FullSeries_PopulatesShortLookbackIndicators()
    {
        // 250 bars is enough for EMA200 to stabilise.
        var quotes = SyntheticQuotes(count: 250);
        var sessionStart = quotes[^1].Date.Date; // start of "today"

        var snap = IndicatorComputer.Compute(quotes, sessionStart);

        snap.AsOfUtc.Should().Be(quotes[^1].Date);
        snap.Ema9.Should().NotBeNull();
        snap.Ema21.Should().NotBeNull();
        snap.Ema50.Should().NotBeNull();
        snap.Ema200.Should().NotBeNull();
        snap.Atr14.Should().NotBeNull();
        snap.Rsi14.Should().NotBeNull();
        snap.BbUpper.Should().NotBeNull();
        snap.BbMid.Should().NotBeNull();
        snap.BbLower.Should().NotBeNull();
        snap.DonchianUpper.Should().NotBeNull();
        snap.DonchianLower.Should().NotBeNull();
        snap.VwapSession.Should().NotBeNull();

        // Sanity checks tied to construction.
        snap.BbLower!.Value.Should().BeLessThan(snap.BbUpper!.Value);
        snap.DonchianLower!.Value.Should().BeLessThanOrEqualTo(snap.DonchianUpper!.Value);
    }

    private static List<Quote> SyntheticQuotes(int count)
    {
        // Deterministic upward-then-noise series — exercises trend (EMA), range
        // (BB), momentum (RSI/ADX), and VWAP without a flat-volume degenerate case.
        var start = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);
        var quotes = new List<Quote>(count);
        for (var i = 0; i < count; i++)
        {
            var price = 30_000m + i * 5m + (decimal)Math.Sin(i / 7.0) * 50m;
            quotes.Add(new Quote
            {
                Date = start.AddMinutes(5 * i),
                Open = price,
                High = price + 10m,
                Low = price - 10m,
                Close = price + 1m,
                Volume = 100m + (decimal)(i % 17),
            });
        }
        return quotes;
    }
}
