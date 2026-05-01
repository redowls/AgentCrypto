using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradingBot.Strategies.Configuration;

namespace TradingBot.Strategies.Channels;

/// <summary>
/// Default <see cref="IGeneratedSignalChannel"/> implementation. Bounded with
/// <see cref="BoundedChannelFullMode.Wait"/> so a stuck downstream stage
/// back-pressures the SignalEngine, which in turn back-pressures the bar-close
/// channel, which in turn pauses the WS ingestor — same chain we use for the
/// kline pipeline.
///
/// Capacity is sized for "burst of 256 signals" — at production rates a single
/// bar boundary across 50 symbols × 3 strategies hits at most 150 candidates,
/// well below the cap.
/// </summary>
public sealed class BoundedGeneratedSignalChannel : IGeneratedSignalChannel
{
    private readonly Channel<GeneratedSignalEvent> _channel;

    public BoundedGeneratedSignalChannel(IOptions<SignalEngineOptions> options)
    {
        Capacity = Math.Max(64, options.Value.GeneratedSignalChannelCapacity);
        _channel = Channel.CreateBounded<GeneratedSignalEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = false, // multiple downstream subscribers may share.
            SingleWriter = true,  // single SignalEngine producer.
        });
    }

    public ChannelReader<GeneratedSignalEvent> Reader => _channel.Reader;
    public ChannelWriter<GeneratedSignalEvent> Writer => _channel.Writer;
    public int CurrentCount => _channel.Reader.CanCount ? _channel.Reader.Count : -1;
    public int Capacity { get; }
}
