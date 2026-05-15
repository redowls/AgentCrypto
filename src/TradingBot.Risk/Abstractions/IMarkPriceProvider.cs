namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Last-traded / mid-mark price for a symbol, used by
/// <see cref="IAccountSnapshotProvider"/> for real-time mark-to-market.
///
/// Implementation order of preference:
/// 1) <c>ILiveCandleCache</c> — the in-progress kline's <c>Close</c>.
/// 2) <c>ICandleRepository.GetLatestOpenTimeAsync</c> + last closed candle.
/// 3) Direct REST ticker call to the Binance gateway.
///
/// Returns null when none of the above can resolve a price (warm-up, or
/// a symbol that isn't being streamed). Callers use this to fall back to
/// the persisted entry price — risk gates degrade safely rather than failing.
/// </summary>
public interface IMarkPriceProvider
{
    Task<decimal?> TryGetMarkPriceAsync(
        string accountType,
        string symbolCode,
        CancellationToken cancellationToken);
}
