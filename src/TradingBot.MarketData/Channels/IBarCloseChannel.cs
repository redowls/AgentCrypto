using System.Threading.Channels;
using TradingBot.Core.Domain;

namespace TradingBot.MarketData.Channels;

/// <summary>
/// Single-process channel the <c>CandlePersistor</c> publishes to once a closed
/// bar is durably stored AND the indicator pre-cache for that (symbol, interval)
/// has been refreshed. The S6 SignalEngine subscribes as the sole reader.
///
/// Ordering: the persistor is the single writer (it consumes the kline channel
/// with SingleReader=true), so events arrive in the order bars close. The bar
/// for a higher TF (1h) is published at most once per hour per symbol — load is
/// trivial. Capacity is bounded so a slow SignalEngine back-pressures the
/// persistor, which in turn back-pressures the WS ingestor — same back-pressure
/// chain as the kline channel.
/// </summary>
public interface IBarCloseChannel
{
    ChannelReader<BarClosedEvent> Reader { get; }
    ChannelWriter<BarClosedEvent> Writer { get; }
    int CurrentCount { get; }
    int Capacity { get; }
}

/// <summary>
/// Notification that a closed bar has been persisted AND its indicator snapshot
/// is available via <see cref="TradingBot.Core.Indicators.IIndicatorEngine"/>.
/// </summary>
/// <param name="SymbolId">FK to <c>dbo.Symbols.SymbolId</c>.</param>
/// <param name="SymbolCode">Ticker (e.g. <c>BTCUSDT</c>) for log readability.</param>
/// <param name="Interval">Bar interval ("1h", "15m", …).</param>
/// <param name="Candle">The just-closed canonical candle.</param>
public sealed record BarClosedEvent(
    int     SymbolId,
    string  SymbolCode,
    string  Interval,
    Candle  Candle);
