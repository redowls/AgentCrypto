using System.Threading.Channels;
using TradingBot.MarketData.Abstractions;

namespace TradingBot.MarketData.Channels;

/// <summary>
/// Single in-process channel that fans WS + REST kline events into the
/// persistor. The channel is bounded with <see cref="BoundedChannelFullMode.Wait"/>
/// so the writer (ingestor) blocks instead of dropping when the consumer
/// (persistor) falls behind. Back-pressure travels back to the WS handler,
/// which is the desired behaviour: we'd rather pause draining the socket than
/// silently lose ticks. (Binance.Net buffers internally a small amount before
/// the underlying socket pauses.)
/// </summary>
public interface IKlineChannel
{
    ChannelReader<KlineEvent> Reader { get; }
    ChannelWriter<KlineEvent> Writer { get; }
    int CurrentCount { get; }
    int Capacity { get; }
}
