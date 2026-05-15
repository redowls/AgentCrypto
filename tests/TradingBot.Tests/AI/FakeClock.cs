using TradingBot.Core.Abstractions;

namespace TradingBot.Tests.AI;

/// Deterministic IClock for time-sensitive AI tests (rate-limit refill,
/// daily-cap rollover, cache TTL). The fixture-style suite advances the
/// clock manually rather than relying on Task.Delay.
internal sealed class FakeClock : IClock
{
    private DateTime _now;
    public FakeClock(DateTime startUtc) => _now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

    public DateTime UtcNow => _now;
    public DateTimeOffset UtcNowOffset => new(_now, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void SetUtc(DateTime t)   => _now = DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
