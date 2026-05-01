using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.Binance;

/// Adapter that exposes a Binance.Net <see cref="UpdateSubscription"/> as our
/// <see cref="IStreamSubscription"/>. Disposal closes the socket subscription.
internal sealed class StreamSubscriptionHandle : IStreamSubscription
{
    private readonly UpdateSubscription _sub;
    private readonly IBinanceSocketClient _client;
    private int _disposed;

    public StreamSubscriptionHandle(UpdateSubscription sub, IBinanceSocketClient client, string streamId)
    {
        _sub = sub;
        _client = client;
        StreamId = streamId;
    }

    public string StreamId { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _client.UnsubscribeAsync(_sub).ConfigureAwait(false);
    }
}
