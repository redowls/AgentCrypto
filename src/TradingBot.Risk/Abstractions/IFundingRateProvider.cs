namespace TradingBot.Risk.Abstractions;

/// <summary>
/// Upcoming funding-rate snapshot for a USDⓈ-M futures symbol. The §8.2 veto
/// "Skip futures entries when |upcoming funding| &gt; 0.05% if we'd be on
/// paying side" is implemented in <c>RiskManager</c>, this seam just fetches
/// the rate.
///
/// Sign convention matches Binance: positive funding rate ⇒ longs pay shorts
/// at the next funding tick (so a long entry near the tick faces a fee).
/// Returns null when the rate is unknown — futures gateway not configured
/// for the account or a non-funding symbol.
/// </summary>
public interface IFundingRateProvider
{
    Task<FundingRateSnapshot?> TryGetUpcomingAsync(string symbolCode, CancellationToken cancellationToken);
}

/// <param name="SymbolCode">Ticker (e.g. BTCUSDT).</param>
/// <param name="Rate">Signed rate as a fraction (0.0001 = 0.01%).</param>
/// <param name="NextFundingTimeUtc">Server-reported time of the next funding tick.</param>
/// <param name="ObservedAtUtc">When we polled the rate (cache key + staleness).</param>
public sealed record FundingRateSnapshot(
    string   SymbolCode,
    decimal  Rate,
    DateTime NextFundingTimeUtc,
    DateTime ObservedAtUtc);
