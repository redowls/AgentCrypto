using TradingBot.Core.Abstractions;

namespace TradingBot.Backtest.Time;

// IClock that returns whatever the backtest replay loop sets. Bit-exact
// determinism depends on every "now" read going through this — never DateTime.UtcNow.
public sealed class SimulatedClock : IClock
{
    private DateTime _now = DateTime.UnixEpoch;
    private readonly object _lock = new();

    public DateTime UtcNow
    {
        get { lock (_lock) return _now; }
    }

    public DateTimeOffset UtcNowOffset => new(UtcNow, TimeSpan.Zero);

    public void Set(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        lock (_lock) _now = utc;
    }
}
