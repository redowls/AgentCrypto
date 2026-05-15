using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TradingBot.Core.Abstractions;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.Funding;

/// <summary>
/// §8.2 funding-rate veto data source. Calls the Binance USDⓈ-M futures
/// premium-index endpoint (<c>/fapi/v1/premiumIndex</c>), which returns the
/// current <c>lastFundingRate</c> and <c>nextFundingTime</c> per symbol.
///
/// Cached for 60 seconds — funding ticks occur every 8h on Binance, so even
/// a 5-minute cache would be safely fresh; we use 60s to amortise REST cost
/// across multiple risk-gate calls in the same minute (e.g. when several
/// signals fire on the same bar close). Binance.Net handles the error
/// envelope; failures fall back to <c>null</c> (no veto).
/// </summary>
public sealed class BinanceFundingRateProvider : IFundingRateProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IBinanceRestClient _rest;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BinanceFundingRateProvider> _log;

    public BinanceFundingRateProvider(
        IBinanceRestClient rest,
        IClock clock,
        IMemoryCache cache,
        ILogger<BinanceFundingRateProvider> log)
    {
        _rest = rest;
        _clock = clock;
        _cache = cache;
        _log = log;
    }

    public async Task<FundingRateSnapshot?> TryGetUpcomingAsync(string symbolCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolCode);

        var key = $"funding:{symbolCode}";
        if (_cache.TryGetValue<FundingRateSnapshot>(key, out var cached) && cached is not null)
            return cached;

        try
        {
            var result = await _rest.UsdFuturesApi.ExchangeData
                .GetMarkPriceAsync(symbolCode, ct: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.Data is null)
            {
                _log.LogDebug(
                    "Funding lookup failed for {Symbol}: {Code} {Msg}",
                    symbolCode, result.Error?.Code, result.Error?.Message);
                return null;
            }

            var d = result.Data;
            var snap = new FundingRateSnapshot(
                SymbolCode:        symbolCode,
                Rate:              d.FundingRate ?? 0m,
                NextFundingTimeUtc: d.NextFundingTime,
                ObservedAtUtc:     _clock.UtcNow);

            _cache.Set(key, snap, CacheTtl);
            return snap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Funding lookup threw for {Symbol}; treating as null (no veto).", symbolCode);
            return null;
        }
    }
}

/// Default no-op funding provider — used when futures gateway isn't
/// configured (spot-only deployments, unit tests). Returns null for every
/// symbol; the risk gate's veto effectively never fires.
public sealed class NullFundingRateProvider : IFundingRateProvider
{
    public Task<FundingRateSnapshot?> TryGetUpcomingAsync(string symbolCode, CancellationToken cancellationToken) =>
        Task.FromResult<FundingRateSnapshot?>(null);
}
