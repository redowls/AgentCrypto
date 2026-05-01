using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.Resilience;

/// Polly v8 pipeline wrapping every signed Binance REST call. Layered (outer →
/// inner): Timeout → Retry (with decorrelated jitter) → Circuit breaker.
/// 429 / 418 are handled by dedicated predicates so they receive the right
/// treatment (Retry-After, kill switch).
public sealed class BinanceResiliencePipeline
{
    private readonly ResiliencePipeline _pipeline;
    private readonly IBinanceKillSwitch _killSwitch;
    private readonly ILogger<BinanceResiliencePipeline> _log;

    public BinanceResiliencePipeline(
        IOptions<BinanceOptions> options,
        IBinanceKillSwitch killSwitch,
        ILogger<BinanceResiliencePipeline> log)
    {
        _killSwitch = killSwitch;
        _log = log;

        var opt = options.Value;

        _pipeline = new ResiliencePipelineBuilder()
            // OUTER: per-call timeout. 8s per spec.
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(opt.RestTimeoutSeconds),
                Name = "Binance.Timeout",
                OnTimeout = args =>
                {
                    _log.LogWarning("Binance REST call timed out after {Timeout}.", args.Timeout);
                    return ValueTask.CompletedTask;
                }
            })
            // RETRY: decorrelated jitter, max 5 attempts, on transient failures.
            .AddRetry(new RetryStrategyOptions
            {
                Name = "Binance.Retry",
                MaxRetryAttempts = 5,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,                       // Polly v8 decorrelated jitter
                Delay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(10),
                ShouldHandle = new PredicateBuilder()
                    .Handle<BinanceApiException>(static ex => ex.IsRetryable && !ex.IsKillSwitch)
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(static ex => ex.InnerException is TimeoutException),
                DelayGenerator = args =>
                {
                    // For 429 / -1003 honour the server-supplied Retry-After.
                    if (args.Outcome.Exception is BinanceApiException bex)
                    {
                        var retryAfter = bex.ParseRetryAfter();
                        if (retryAfter is { } ra)
                            return ValueTask.FromResult<TimeSpan?>(ra);
                    }
                    return ValueTask.FromResult<TimeSpan?>(null);  // fall through to default jittered delay
                },
                OnRetry = args =>
                {
                    _log.LogWarning(args.Outcome.Exception,
                        "Binance retry #{Attempt} after {Delay} (op={Operation}).",
                        args.AttemptNumber, args.RetryDelay,
                        (args.Outcome.Exception as BinanceApiException)?.Operation ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            // CIRCUIT BREAKER: 50% failure ratio over 30s window with at least
            // 8 reqs sampled; 1-minute break.
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                Name = "Binance.CircuitBreaker",
                FailureRatio = 0.5,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<BinanceApiException>(static ex => ex.IsRetryable && !ex.IsKillSwitch)
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<SocketException>(),
                OnOpened = args =>
                {
                    _log.LogError("Binance circuit breaker OPENED for {Break}.", args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _log.LogInformation("Binance circuit breaker CLOSED.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    _log.LogInformation("Binance circuit breaker HALF-OPEN.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// Run the supplied delegate through the full resilience pipeline. The
    /// kill-switch is checked before the call and tripped if HTTP 418 is seen.
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_killSwitch.IsTripped)
            throw new BinanceKillSwitchTrippedException(_killSwitch.Reason ?? "tripped", _killSwitch.RetryAfterUtc);

        try
        {
            return await _pipeline.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (BinanceApiException bex) when (bex.IsKillSwitch)
        {
            var retryAfterUtc = bex.ParseRetryAfter() is { } ra ? DateTime.UtcNow.Add(ra) : (DateTime?)null;
            _killSwitch.Trip($"HTTP 418 from {operation}", retryAfterUtc);
            throw new BinanceKillSwitchTrippedException(bex.Message, retryAfterUtc);
        }
    }

    public Task ExecuteAsync(
        Func<CancellationToken, ValueTask> action,
        string operation,
        CancellationToken cancellationToken) =>
        ExecuteAsync<int>(async ct => { await action(ct).ConfigureAwait(false); return 0; }, operation, cancellationToken);
}

public sealed class BinanceKillSwitchTrippedException : Exception
{
    public BinanceKillSwitchTrippedException(string reason, DateTime? retryAfterUtc)
        : base($"Binance kill switch tripped: {reason} (retry after {retryAfterUtc:O}).")
    {
        Reason = reason;
        RetryAfterUtc = retryAfterUtc;
    }

    public string Reason { get; }
    public DateTime? RetryAfterUtc { get; }
}
