namespace TradingBot.Exchange.Abstractions;

public sealed record StreamHealth(
    string    StreamId,
    AccountType Account,
    DateTime?   LastEventUtc,
    bool        IsStale,
    int         ReconnectCount,
    string?     LastError);

public interface IBinanceWebSocketManager
{
    Task<IStreamSubscription> SubscribeKlineAsync(
        AccountType account,
        string symbol,
        string interval,
        Func<KlineData, ValueTask> onKline,
        CancellationToken cancellationToken);

    Task<IStreamSubscription> SubscribeUserDataAsync(
        AccountType account,
        Func<UserDataEvent, ValueTask> onEvent,
        CancellationToken cancellationToken);

    IReadOnlyList<StreamHealth> Health();
}

public interface IWebSocketAlertSink
{
    void RaiseStaleStream(StreamHealth health);
    void RaiseListenKeyExpired(AccountType account);
}
