using Microsoft.Extensions.Logging;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.MarketData.Abstractions;
using TradingBot.MarketData.Caching;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.Pricing;

/// <summary>
/// Layered mark-price resolution:
///   1. <see cref="ILiveCandleCache"/> — in-progress kline close (sub-second freshness).
///   2. <see cref="ICandleRepository"/> — most recent closed bar's close.
///
/// The futures gateway has its own /premiumIndex and /ticker/price endpoints
/// but we deliberately don't call them here — that path costs a REST round-trip
/// per position per snapshot, and the real-time mark for our purposes is the
/// in-progress kline close. The reconciliation service still uses authoritative
/// gateway data for drift checks (S8).
/// </summary>
public sealed class MarkPriceProvider : IMarkPriceProvider
{
    /// Intervals tried in order — prefer the fastest available stream. The
    /// 1m cache is the hottest path; 5m/15m/1h are fallbacks for symbols
    /// only subscribed on a slower TF.
    private static readonly string[] PreferredIntervals =
    {
        CandleIntervals.OneMinute,
        CandleIntervals.FiveMinutes,
        CandleIntervals.FifteenMinutes,
        CandleIntervals.OneHour,
    };

    private readonly ILiveCandleCache _liveCache;
    private readonly ICandleRepository _candles;
    private readonly ISymbolRepository _symbols;
    private readonly ILogger<MarkPriceProvider> _log;

    public MarkPriceProvider(
        ILiveCandleCache liveCache,
        ICandleRepository candles,
        ISymbolRepository symbols,
        ILogger<MarkPriceProvider> log)
    {
        _liveCache = liveCache;
        _candles = candles;
        _symbols = symbols;
        _log = log;
    }

    public async Task<decimal?> TryGetMarkPriceAsync(
        string accountType,
        string symbolCode,
        CancellationToken cancellationToken)
    {
        // Live cache lookup — try the preferred intervals top-down.
        foreach (var tf in PreferredIntervals)
        {
            try
            {
                var live = await _liveCache.TryGetAsync(symbolCode, tf, cancellationToken).ConfigureAwait(false);
                if (live is not null && live.Kline.Close > 0m)
                    return live.Kline.Close;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Live cache lookup failed for {Symbol}/{Tf}", symbolCode, tf);
            }
        }

        // Fall back to the latest closed candle in dbo.Candles.
        var exchange = string.Equals(accountType, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase)
            ? Exchanges.BinanceUmFut
            : Exchanges.BinanceSpot;

        var symbol = await _symbols.GetByExchangeAndCodeAsync(exchange, symbolCode, cancellationToken).ConfigureAwait(false);
        if (symbol is null) return null;

        foreach (var tf in PreferredIntervals)
        {
            var latest = await _candles.GetLatestOpenTimeAsync(symbol.SymbolId, tf, cancellationToken).ConfigureAwait(false);
            if (latest is null) continue;

            var bar = await _candles.GetAsync(symbol.SymbolId, tf, latest.Value, cancellationToken).ConfigureAwait(false);
            if (bar is not null && bar.Close > 0m)
                return bar.Close;
        }

        return null;
    }
}
