namespace TradingBot.Exchange.Abstractions;

public enum WebSocketHealthStatus
{
    NotStarted = 0,
    Connected = 1,
    Reconnecting = 2,
    Disconnected = 3,
}

public sealed record WebSocketHealthSnapshot(
    WebSocketHealthStatus Status,
    int ActiveStreams,
    DateTime? LastEventUtc,
    string? LastError);

/// Reports current health of WebSocket subscriptions. In S1 this is a stub
/// that always reports NotStarted; the real implementation is wired in S3.2.
public interface IWebSocketHealthProbe
{
    WebSocketHealthSnapshot Snapshot();
}
