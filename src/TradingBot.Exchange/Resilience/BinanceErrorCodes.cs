namespace TradingBot.Exchange.Resilience;

/// Binance error codes we treat specially in the resilience pipeline. Sourced
/// from the Binance docs (Spot + USDⓈ-M Futures share these for the codes we
/// care about).
public static class BinanceErrorCodes
{
    /// DISCONNECTED — the request socket was forcibly closed; safe to retry.
    public const int Disconnected     = -1001;

    /// TOO_MANY_REQUESTS — IP-level rate limit. Honour Retry-After.
    public const int TooManyRequests  = -1003;

    /// INVALID_TIMESTAMP — local clock skew vs. server. Caller must resync.
    public const int InvalidTimestamp = -1021;

    /// INVALID_LISTEN_KEY — listen key was rotated or expired.
    public const int InvalidListenKey = -1125;

    /// REQUEST_WEIGHT exhausted; treat like 429.
    public const int RequestWeight    = -1015;

    /// HTTP statuses we react to.
    public const int Http429RateLimit = 429;

    /// HTTP 418 — IP banned. Trip the kill switch.
    public const int Http418Banned    = 418;

    /// Codes that should be retried by the Polly pipeline (transient).
    public static readonly IReadOnlySet<int> Retryable = new HashSet<int>
    {
        Disconnected,
        TooManyRequests,
        InvalidTimestamp,
        RequestWeight,
    };
}
