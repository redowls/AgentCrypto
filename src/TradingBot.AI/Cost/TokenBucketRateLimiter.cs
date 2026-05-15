using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace TradingBot.AI.Cost;

/// <summary>
/// §5.5 token bucket — capacity = <c>RequestsPerMinute</c>, refill rate
/// = <c>capacity / 60s</c>. Lazy refill: each <see cref="WaitAsync"/> call
/// computes how many tokens have accrued since the last sample, caps at
/// capacity, and either consumes one or blocks for the time-to-next-token.
/// Lock-based (no allocations on the hot path); a <see cref="SemaphoreSlim"/>
/// would also work but the bookkeeping is just as clean here.
/// </summary>
internal sealed class TokenBucketRateLimiter : IAiRateLimiter
{
    private readonly int     _capacity;
    private readonly double  _refillPerSecond;
    private readonly IClock  _clock;
    private readonly object  _lock = new();

    private double  _tokens;
    private DateTime _lastRefillUtc;

    public TokenBucketRateLimiter(IOptions<ClaudeOptions> options, IClock clock)
    {
        _capacity        = options.Value.RequestsPerMinute;
        _refillPerSecond = _capacity / 60.0;
        _clock           = clock;
        _tokens          = _capacity;          // start full so the first burst doesn't pay a wait
        _lastRefillUtc   = _clock.UtcNow;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan waitFor;
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                var deficit = 1.0 - _tokens;
                waitFor = TimeSpan.FromSeconds(deficit / _refillPerSecond);
                if (waitFor < TimeSpan.FromMilliseconds(1))
                    waitFor = TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(waitFor, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _clock.UtcNow;
        var elapsed = (now - _lastRefillUtc).TotalSeconds;
        if (elapsed <= 0) return;

        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);
        _lastRefillUtc = now;
    }
}
