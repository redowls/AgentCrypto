using Microsoft.Extensions.Logging;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.Resilience;

// BinanceKillSwitch stays in Exchange. IAlertSink lives in TradingBot.Risk, which
// already references Exchange — making Exchange reference Risk would cycle. The
// CRITICAL alert for kill-switch trips is fired via Risk.KillSwitch (which mirrors
// state from Binance's switch on every read of IsTripped); the BinanceKillSwitch
// HealthCheck (§11) also exposes the trip state to /health/readiness.
public sealed class BinanceKillSwitch(ILogger<BinanceKillSwitch> logger) : IBinanceKillSwitch
{
    private readonly object _gate = new();
    private bool _tripped;
    private DateTime? _trippedAtUtc;
    private string? _reason;
    private DateTime? _retryAfterUtc;

    public bool IsTripped { get { lock (_gate) return _tripped; } }
    public DateTime? TrippedAtUtc { get { lock (_gate) return _trippedAtUtc; } }
    public string? Reason { get { lock (_gate) return _reason; } }
    public DateTime? RetryAfterUtc { get { lock (_gate) return _retryAfterUtc; } }

    public void Trip(string reason, DateTime? retryAfterUtc)
    {
        lock (_gate)
        {
            if (_tripped) return;
            _tripped = true;
            _trippedAtUtc = DateTime.UtcNow;
            _reason = reason;
            _retryAfterUtc = retryAfterUtc;
        }
        logger.LogCritical(
            "BINANCE KILL SWITCH TRIPPED: {Reason}. Retry after {RetryAfter:O}. All trading halted.",
            reason, retryAfterUtc);
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (!_tripped) return;
            _tripped = false;
            _trippedAtUtc = null;
            _reason = null;
            _retryAfterUtc = null;
        }
        logger.LogWarning("Binance kill switch manually reset.");
    }
}
