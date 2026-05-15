namespace TradingBot.AI.Abstractions;

/// <summary>
/// Token-bucket limiter — default 10 RPM per §5.5. Implementations refill
/// tokens lazily on each <see cref="WaitAsync"/> call against a monotonic
/// clock; the bucket holds at most <c>RequestsPerMinute</c> tokens at once.
/// </summary>
public interface IAiRateLimiter
{
    Task WaitAsync(CancellationToken cancellationToken);
}
