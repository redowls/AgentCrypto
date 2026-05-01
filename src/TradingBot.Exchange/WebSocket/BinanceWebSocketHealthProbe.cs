using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.WebSocket;

/// Replaces the S1 stub. Reports an aggregate snapshot derived from the live
/// stream registry: connection state, count of active streams, latest event
/// timestamp across all streams, and the most recent error message (if any).
public sealed class BinanceWebSocketHealthProbe : IWebSocketHealthProbe
{
    private readonly StreamRegistry _registry;
    private readonly IOptionsMonitor<BinanceOptions> _options;

    public BinanceWebSocketHealthProbe(StreamRegistry registry, IOptionsMonitor<BinanceOptions> options)
    {
        _registry = registry;
        _options = options;
    }

    public WebSocketHealthSnapshot Snapshot()
    {
        var streams = _registry.All();
        if (streams.Count == 0)
            return new WebSocketHealthSnapshot(WebSocketHealthStatus.NotStarted, 0, null, null);

        var staleAfter = _options.CurrentValue.WebSocketStaleAfter;
        var now = DateTime.UtcNow;

        DateTime? mostRecent = null;
        string? lastError = null;
        var anyStale = false;
        var anyHealthy = false;

        foreach (var s in streams)
        {
            var last = s.LastEventUtc;
            if (last is { } l)
            {
                if (mostRecent is null || l > mostRecent) mostRecent = l;
                if ((now - l) > staleAfter) anyStale = true; else anyHealthy = true;
            }
            else
            {
                anyStale = true;
            }

            if (s.LastError is not null) lastError = s.LastError;
        }

        var status = anyStale
            ? (anyHealthy ? WebSocketHealthStatus.Reconnecting : WebSocketHealthStatus.Disconnected)
            : WebSocketHealthStatus.Connected;

        return new WebSocketHealthSnapshot(status, streams.Count, mostRecent, lastError);
    }
}
