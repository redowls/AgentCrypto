using System.ComponentModel.DataAnnotations;

namespace TradingBot.Exchange.Configuration;

public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    /// When true, both REST and WebSocket clients target the Binance Testnet.
    public bool UseTestnet { get; set; } = true;

    /// Enable the SPOT account paths — gateway, REST client, userData WS.
    public bool EnableSpot { get; set; } = true;

    /// Enable the USDⓈ-M Futures account paths.
    public bool EnableUsdmFutures { get; set; } = true;

    /// Receive window for signed REST requests, in milliseconds.
    [Range(1_000, 60_000)]
    public int RecvWindowMs { get; set; } = 5_000;

    /// Per-call REST timeout enforced by the Polly pipeline.
    [Range(1, 60)]
    public int RestTimeoutSeconds { get; set; } = 8;

    /// Reference data refresh time (UTC).
    public TimeSpan ReferenceDataDailyRefreshUtc { get; set; } = new(0, 5, 0);

    /// listenKey keepalive interval. Binance expires listenKey after 60 minutes
    /// without keepalive; keep this comfortably below.
    public TimeSpan ListenKeyKeepaliveInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// Max time without any message on a subscribed stream before the watchdog
    /// raises a CRITICAL alert.
    public TimeSpan WebSocketStaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// Watchdog evaluation period.
    public TimeSpan WebSocketWatchdogInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// Refuse to start if the API key carries any of these permissions.
    /// WITHDRAW must never be enabled. We also reject MARGIN unless the user
    /// explicitly opts in via configuration.
    public bool AllowMarginPermission { get; set; }
}
