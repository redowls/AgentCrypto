namespace TradingBot.Exchange.Abstractions;

/// Tripped when Binance returns HTTP 418 (IP-banned for repeated violations of
/// rate limits). All trading must halt; the runbook in §6.4 / §9 dictates the
/// recovery process.
public interface IBinanceKillSwitch
{
    bool IsTripped { get; }
    DateTime? TrippedAtUtc { get; }
    string? Reason { get; }
    DateTime? RetryAfterUtc { get; }

    void Trip(string reason, DateTime? retryAfterUtc);
    void Reset();
}
