using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.WebSocket;

/// Tracks all active WebSocket subscriptions and exposes per-stream health.
/// Auto-reconnect is delegated to Binance.Net (it reconnects with exponential
/// backoff out of the box — we explicitly verify this in DI configuration);
/// this manager wraps each subscription so that the watchdog can detect
/// stalls (e.g. silent disconnects, listenKey expiries that didn't fire an
/// event) by comparing wall time against the last received message.
public sealed class BinanceWebSocketManager : IBinanceWebSocketManager
{
    private readonly IBinanceGatewayResolver _gateways;
    private readonly StreamRegistry _registry;
    private readonly IListenKeyRegistry _listenKeys;
    private readonly ILogger<BinanceWebSocketManager> _log;

    public BinanceWebSocketManager(
        IBinanceGatewayResolver gateways,
        StreamRegistry registry,
        IListenKeyRegistry listenKeys,
        ILogger<BinanceWebSocketManager> log)
    {
        _gateways = gateways;
        _registry = registry;
        _listenKeys = listenKeys;
        _log = log;
    }

    public async Task<IStreamSubscription> SubscribeKlineAsync(
        AccountType account,
        string symbol,
        string interval,
        Func<KlineData, ValueTask> onKline,
        CancellationToken cancellationToken)
    {
        var streamId = $"{Prefix(account)}.kline.{symbol.ToUpperInvariant()}.{interval}";
        var record = _registry.Register(streamId, account);
        record.MarkEvent(); // initialise so the watchdog grace period applies from now

        var gateway = _gateways.Get(account);
        var sub = await gateway.SubscribeKlineAsync(symbol, interval, async k =>
        {
            record.MarkEvent();
            try
            {
                await onKline(k).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                record.LastError = ex.Message;
                _log.LogError(ex, "Kline handler threw on {StreamId}", streamId);
            }
        }, cancellationToken).ConfigureAwait(false);

        record.Subscription = sub;
        _log.LogInformation("Subscribed kline {StreamId}.", streamId);

        return new ManagedSubscription(sub, () => _registry.TryRemove(streamId, out _));
    }

    public async Task<IStreamSubscription> SubscribeUserDataAsync(
        AccountType account,
        Func<UserDataEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var gateway = _gateways.Get(account);
        var listenKey = await gateway.StartUserDataStreamAsync(cancellationToken).ConfigureAwait(false);

        var streamId = $"{Prefix(account)}.userData";
        var record = _registry.Register(streamId, account);
        record.MarkEvent();

        var sub = await gateway.SubscribeUserDataAsync(listenKey, async evt =>
        {
            record.MarkEvent();
            try
            {
                await onEvent(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                record.LastError = ex.Message;
                _log.LogError(ex, "UserData handler threw on {StreamId}", streamId);
            }
        }, cancellationToken).ConfigureAwait(false);

        record.Subscription = sub;
        _listenKeys.Register(account, listenKey);
        _log.LogInformation("Subscribed userData {StreamId} (listenKey prefix={Prefix}).",
            streamId, listenKey[..Math.Min(8, listenKey.Length)]);

        var managed = new ManagedUserDataSubscription(
            sub, gateway, listenKey, () =>
            {
                _registry.TryRemove(streamId, out _);
                _listenKeys.Unregister(account);
            });
        return managed;
    }

    public IReadOnlyList<StreamHealth> Health()
    {
        var now = DateTime.UtcNow;
        return _registry.All()
            .Select(s => s.ToHealth(TimeSpan.FromSeconds(60), now))
            .ToList();
    }

    private static string Prefix(AccountType account) => account == AccountType.Spot ? "spot" : "fut";

    private sealed class ManagedSubscription : IStreamSubscription
    {
        private readonly IStreamSubscription _inner;
        private readonly Action _onDispose;
        private int _disposed;

        public ManagedSubscription(IStreamSubscription inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public string StreamId => _inner.StreamId;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _onDispose();
        }
    }

    private sealed class ManagedUserDataSubscription : IStreamSubscription
    {
        private readonly IStreamSubscription _inner;
        private readonly IBinanceGateway _gateway;
        private readonly string _listenKey;
        private readonly Action _onDispose;
        private int _disposed;

        public ManagedUserDataSubscription(
            IStreamSubscription inner, IBinanceGateway gateway, string listenKey, Action onDispose)
        {
            _inner = inner;
            _gateway = gateway;
            _listenKey = listenKey;
            _onDispose = onDispose;
        }

        public string StreamId => _inner.StreamId;
        internal string ListenKey => _listenKey;
        internal IBinanceGateway Gateway => _gateway;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _inner.DisposeAsync().ConfigureAwait(false);
            try { await _gateway.CloseUserDataStreamAsync(_listenKey, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            _onDispose();
        }
    }
}
