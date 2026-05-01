using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.Channels;

public sealed class BoundedKlineChannel : IKlineChannel
{
    private readonly Channel<KlineEvent> _channel;

    public BoundedKlineChannel(IOptions<MarketDataOptions> options)
    {
        Capacity = options.Value.ChannelCapacity;
        _channel = Channel.CreateBounded<KlineEvent>(new BoundedChannelOptions(Capacity)
        {
            // Block on full → back-pressure to WS handler. Spec §1.3.
            FullMode = BoundedChannelFullMode.Wait,
            // Single persistor consumer; multiple ingestor producers (one per
            // (Symbol, Interval) subscription run their callbacks concurrently).
            SingleReader = true,
            SingleWriter = false,
            // Ordering across symbols doesn't matter — the persistor MERGEs by
            // natural key, so out-of-order arrivals between distinct streams
            // resolve correctly. AllowSynchronousContinuations stays default
            // (false) to prevent the WS callback thread from running the
            // persistor work synchronously.
        });
    }

    public ChannelReader<KlineEvent> Reader => _channel.Reader;
    public ChannelWriter<KlineEvent> Writer => _channel.Writer;
    public int CurrentCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;
    public int Capacity { get; }
}
