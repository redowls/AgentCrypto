using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingBot.MarketData.Configuration;

namespace TradingBot.MarketData.Channels;

/// <summary>
/// Default <see cref="IBarCloseChannel"/> implementation. Bounded with
/// <see cref="BoundedChannelFullMode.Wait"/> so a stuck SignalEngine back-pressures
/// the persistor instead of dropping bar-close notifications. Capacity reuses
/// <see cref="MarketDataOptions.ChannelCapacity"/> — bar closes are infrequent,
/// so even a small fraction of the kline-channel capacity is comfortable.
/// </summary>
public sealed class BoundedBarCloseChannel : IBarCloseChannel
{
    private readonly Channel<BarClosedEvent> _channel;

    public BoundedBarCloseChannel(IOptions<MarketDataOptions> options)
    {
        // Cap at 1024 events: at 1m × 50 symbols that's 17 minutes of headroom.
        // Strategies typically run on 15m+, so capacity is enormous in practice.
        Capacity = Math.Max(64, Math.Min(options.Value.ChannelCapacity / 8, 1024));
        _channel = Channel.CreateBounded<BarClosedEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true, // SignalEngine is the sole consumer.
            SingleWriter = true, // CandlePersistor is the sole producer.
        });
    }

    public ChannelReader<BarClosedEvent> Reader => _channel.Reader;
    public ChannelWriter<BarClosedEvent> Writer => _channel.Writer;
    public int CurrentCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;
    public int Capacity { get; }
}
