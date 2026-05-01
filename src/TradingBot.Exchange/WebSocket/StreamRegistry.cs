using System.Collections.Concurrent;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.WebSocket;

/// Stream record. The watchdog reads the last-event timestamp atomically and
/// compares against `now - WebSocketStaleAfter`.
public sealed class StreamRecord
{
    private long _lastEventTicks;
    private int  _reconnects;

    public required string StreamId { get; init; }
    public required AccountType Account { get; init; }
    public string? LastError { get; set; }
    public IStreamSubscription? Subscription { get; set; }

    public void MarkEvent() => Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);

    public DateTime? LastEventUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastEventTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public int Reconnects => Volatile.Read(ref _reconnects);
    public void IncrementReconnects() => Interlocked.Increment(ref _reconnects);

    public StreamHealth ToHealth(TimeSpan staleAfter, DateTime now) =>
        new(StreamId, Account, LastEventUtc,
            IsStale: LastEventUtc is { } last && (now - last) > staleAfter,
            Reconnects, LastError);
}

public sealed class StreamRegistry
{
    private readonly ConcurrentDictionary<string, StreamRecord> _streams = new();

    public StreamRecord Register(string streamId, AccountType account)
    {
        var rec = new StreamRecord { StreamId = streamId, Account = account };
        _streams[streamId] = rec;
        return rec;
    }

    public bool TryRemove(string streamId, out StreamRecord? rec) => _streams.TryRemove(streamId, out rec);

    public IReadOnlyCollection<StreamRecord> All() => _streams.Values.ToArray();
}
