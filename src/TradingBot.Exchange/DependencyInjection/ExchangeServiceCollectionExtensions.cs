using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingBot.Core.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Binance;
using TradingBot.Exchange.Configuration;
using TradingBot.Exchange.ReferenceData;
using TradingBot.Exchange.Resilience;
using TradingBot.Exchange.WebSocket;

namespace TradingBot.Exchange.DependencyInjection;

public static class ExchangeServiceCollectionExtensions
{
    /// Wires Binance.Net REST + Socket clients, the gateway implementations,
    /// the resilience pipeline, the reference-data service (with daily refresh
    /// hosted service), the WebSocket manager + watchdog, listenKey keepalive,
    /// and the startup permission verifier.
    ///
    /// API credentials are read via <see cref="ISecretsProvider"/> only — never
    /// from plain configuration — to satisfy §9.7 key-safety rules. Binance.Net
    /// auto-reconnects WebSocket subscriptions on disconnect with exponential
    /// backoff out of the box; we leave that default in place but explicitly
    /// confirm by configuring <c>SocketOptions.ReconnectPolicy</c>.
    public static IServiceCollection AddBinanceExchange(
        this IServiceCollection services,
        IConfiguration configuration,
        ISecretsProvider secrets)
    {
        // The Worker host already calls AddOptions<BinanceOptions>().Bind(...)
        // .ValidateDataAnnotations() once at startup (Microsoft.Extensions.
        // Options.DataAnnotations is referenced there, not here). Calling
        // .Bind() again is idempotent, so we keep this lightweight here.
        services.AddOptions<BinanceOptions>()
            .Bind(configuration.GetSection(BinanceOptions.SectionName));

        var useTestnet = configuration.GetValue<bool>($"{BinanceOptions.SectionName}:UseTestnet", defaultValue: true);
        var recvWindowMs = configuration.GetValue<int>($"{BinanceOptions.SectionName}:RecvWindowMs", defaultValue: 5_000);

        var creds = ResolveCredentials(secrets);

        services.AddBinance(restOptions =>
        {
            if (creds is not null) restOptions.ApiCredentials = creds;
            restOptions.ReceiveWindow = TimeSpan.FromMilliseconds(recvWindowMs);
            if (useTestnet) restOptions.Environment = BinanceEnvironment.Testnet;
        }, socketOptions =>
        {
            if (creds is not null) socketOptions.ApiCredentials = creds;
            if (useTestnet) socketOptions.Environment = BinanceEnvironment.Testnet;
            // Verified-on-build: Binance.Net 10.6 uses a single ReconnectPolicy
            // enum + ReconnectInterval base. ExponentialBackoff doubles the
            // interval up to a cap of 5 attempts (2s → 4s → 8s → 16s → 32s),
            // then cycles. That matches the spec for "auto-reconnect with
            // exponential backoff". The library reconnects forever — there is
            // no separate AutoReconnect / MaxReconnectTries flag in 10.6.
            socketOptions.ReconnectInterval = TimeSpan.FromSeconds(2);
            socketOptions.ReconnectPolicy = ReconnectPolicy.ExponentialBackoff;
        });

        services.AddSingleton<IBinanceKillSwitch, BinanceKillSwitch>();
        services.AddSingleton<BinanceResiliencePipeline>();

        services.AddSingleton<BinanceSpotGateway>();
        services.AddSingleton<BinanceFuturesGateway>();
        services.AddSingleton<IBinanceGatewayResolver, BinanceGatewayResolver>();
        services.AddSingleton<IExchangePing, BinanceExchangePing>();

        services.AddSingleton<SymbolFilters>();
        services.AddSingleton<ISymbolFilters>(sp => sp.GetRequiredService<SymbolFilters>());
        services.AddSingleton<IReferenceDataService, ReferenceDataService>();
        services.AddHostedService<ReferenceDataRefreshHostedService>();

        services.AddSingleton<StreamRegistry>();
        services.AddSingleton<ListenKeyKeepaliveService>();
        services.AddSingleton<IListenKeyRegistry>(sp => sp.GetRequiredService<ListenKeyKeepaliveService>());
        services.AddHostedService(sp => sp.GetRequiredService<ListenKeyKeepaliveService>());
        services.AddSingleton<IBinanceWebSocketManager, BinanceWebSocketManager>();
        services.AddSingleton<IWebSocketHealthProbe, BinanceWebSocketHealthProbe>();
        services.AddSingleton<IWebSocketAlertSink, LoggingWebSocketAlertSink>();
        services.AddHostedService<WebSocketWatchdog>();

        services.AddSingleton<KeyPermissionVerifier>();
        services.AddHostedService<KeyPermissionVerifierHostedService>();

        return services;
    }

    private static ApiCredentials? ResolveCredentials(ISecretsProvider secrets)
    {
        if (secrets.TryGet("Binance:ApiKey", out var apiKey) &&
            secrets.TryGet("Binance:ApiSecret", out var apiSecret) &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !string.IsNullOrWhiteSpace(apiSecret))
        {
            return new ApiCredentials(apiKey, apiSecret);
        }
        return null;
    }
}
