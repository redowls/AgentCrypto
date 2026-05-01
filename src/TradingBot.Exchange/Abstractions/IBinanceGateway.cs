namespace TradingBot.Exchange.Abstractions;

/// Single seam over the Binance REST + WebSocket surface used by the bot.
/// Two implementations: one for SPOT, one for USDⓈ-M Futures. The execution
/// engine selects the correct gateway via <see cref="IBinanceGatewayResolver"/>.
public interface IBinanceGateway
{
    AccountType Account { get; }

    Task<ExchangeInfoSnapshot> GetExchangeInfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KlineData>> GetKlinesAsync(
        string symbol,
        string interval,
        DateTime? startUtc,
        DateTime? endUtc,
        int limit,
        CancellationToken cancellationToken);

    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken);

    Task<OrderResult> CancelOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken);

    Task<OrderResult?> GetOrderAsync(string symbol, string clientOrderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderResult>> GetOpenOrdersAsync(string? symbol, CancellationToken cancellationToken);

    Task<AccountInfoSnapshot> GetAccountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<UserTrade>> GetUserTradesAsync(string symbol, long? fromTradeId, CancellationToken cancellationToken);

    /// Allocates a listen key for the user-data stream. Must be kept alive via
    /// the WebSocket manager (PUT every <30 minutes).
    Task<string> StartUserDataStreamAsync(CancellationToken cancellationToken);

    Task KeepAliveUserDataStreamAsync(string listenKey, CancellationToken cancellationToken);

    Task CloseUserDataStreamAsync(string listenKey, CancellationToken cancellationToken);

    Task<IStreamSubscription> SubscribeKlineAsync(
        string symbol,
        string interval,
        Func<KlineData, ValueTask> onKline,
        CancellationToken cancellationToken);

    Task<IStreamSubscription> SubscribeUserDataAsync(
        string listenKey,
        Func<UserDataEvent, ValueTask> onEvent,
        CancellationToken cancellationToken);
}

/// Resolves the gateway for a given <see cref="AccountType"/>; the bot keeps
/// one instance per account type for the lifetime of the process.
public interface IBinanceGatewayResolver
{
    IBinanceGateway Get(AccountType account);
}
