using CryptoExchange.Net.Objects;

namespace TradingBot.Exchange.Resilience;

/// Wraps Binance.Net result types into something the Polly pipeline can
/// reason about. Successes return data; failures are turned into typed
/// exceptions so the strategy predicates can match by error code.
///
/// Three result shapes exist in the underlying SDK:
///   • <see cref="WebCallResult{T}"/>  — REST calls returning data.
///   • <see cref="WebCallResult"/>     — REST calls without a body.
///   • <see cref="CallResult{T}"/>     — socket subscribe operations.
public static class BinanceCallOutcome
{
    public static T Unwrap<T>(WebCallResult<T> result, string operation)
    {
        if (result.Success && result.Data is not null)
            return result.Data;

        throw ToException(result, operation);
    }

    public static T Unwrap<T>(CallResult<T> result, string operation)
    {
        if (result.Success && result.Data is not null)
            return result.Data;

        throw ToException(result, operation);
    }

    public static void EnsureSuccess(WebCallResult result, string operation)
    {
        if (!result.Success)
            throw ToException(result, operation);
    }

    public static BinanceApiException ToException<T>(WebCallResult<T> result, string operation)
    {
        var code = result.Error?.Code ?? 0;
        var message = result.Error?.Message ?? "unknown";
        var http = (int?)result.ResponseStatusCode ?? 0;
        return new BinanceApiException(operation, code, http, message, result.Error?.Data?.ToString());
    }

    public static BinanceApiException ToException(WebCallResult result, string operation)
    {
        var code = result.Error?.Code ?? 0;
        var message = result.Error?.Message ?? "unknown";
        var http = (int?)result.ResponseStatusCode ?? 0;
        return new BinanceApiException(operation, code, http, message, result.Error?.Data?.ToString());
    }

    public static BinanceApiException ToException<T>(CallResult<T> result, string operation)
    {
        var code = result.Error?.Code ?? 0;
        var message = result.Error?.Message ?? "unknown";
        return new BinanceApiException(operation, code, 0, message, result.Error?.Data?.ToString());
    }
}

public sealed class BinanceApiException : Exception
{
    public BinanceApiException(string operation, int code, int httpStatus, string message, string? raw)
        : base($"[Binance:{operation}] http={httpStatus} code={code}: {message}")
    {
        Operation = operation;
        ErrorCode = code;
        HttpStatus = httpStatus;
        RawError = raw;
    }

    public string Operation { get; }
    public int ErrorCode { get; }
    public int HttpStatus { get; }
    public string? RawError { get; }

    public bool IsRetryable =>
        BinanceErrorCodes.Retryable.Contains(ErrorCode) ||
        HttpStatus is >= 500 and <= 599 ||
        HttpStatus == BinanceErrorCodes.Http429RateLimit;

    public bool IsKillSwitch => HttpStatus == BinanceErrorCodes.Http418Banned;

    /// Best-effort parse of a Retry-After header value embedded in the raw
    /// error payload. Returns null if absent or unparseable.
    public TimeSpan? ParseRetryAfter()
    {
        if (string.IsNullOrEmpty(RawError)) return null;
        var marker = "Retry-After:";
        var idx = RawError.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = RawError.AsSpan(idx + marker.Length).Trim();
        var end = rest.IndexOfAny(new[] { ',', ';', '\n', '\r' });
        if (end >= 0) rest = rest[..end];
        return int.TryParse(rest, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
    }
}
