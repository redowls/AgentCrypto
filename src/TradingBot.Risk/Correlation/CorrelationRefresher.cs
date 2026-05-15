using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;

namespace TradingBot.Risk.Correlation;

/// <summary>
/// Build the §8.3 correlation matrix from <c>dbo.Candles</c> daily closes,
/// run greedy clustering, persist the upper-triangle pairs + cluster
/// assignments via <see cref="ICorrelationRepository.ReplaceSnapshotAsync"/>.
///
/// Universe selection:
///   • <see cref="RiskOptions.CorrelationUniverse"/> if non-empty (canonical
///     symbol codes, e.g. "BTCUSDT"),
///   • otherwise every active row in dbo.Symbols.
///
/// Lookback default 30 days. We pull 1d candles directly from the canonical
/// table (no Redis), align by OpenTime, and skip symbols with &lt;
/// <see cref="MinSamples"/> overlapping closes — too few points produce
/// noisy correlations that pollute the cluster.
/// </summary>
public sealed class CorrelationRefresher : ICorrelationRefresher
{
    /// Minimum overlapping closes required to compute a pair correlation.
    /// At 30d lookback that's 24/30 = 80% coverage, leaving room for one
    /// missing-data day per symbol on either side without disqualifying.
    public const int MinSamples = 24;

    private readonly ISymbolRepository _symbols;
    private readonly ICandleRepository _candles;
    private readonly ICorrelationRepository _correlations;
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<CorrelationRefresher> _log;

    public CorrelationRefresher(
        ISymbolRepository symbols,
        ICandleRepository candles,
        ICorrelationRepository correlations,
        IOptionsMonitor<RiskOptions> options,
        IClock clock,
        ILogger<CorrelationRefresher> log)
    {
        _symbols = symbols;
        _candles = candles;
        _correlations = correlations;
        _options = options;
        _clock = clock;
        _log = log;
    }

    public async Task RefreshAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var universe = await ResolveUniverseAsync(opts, cancellationToken).ConfigureAwait(false);
        if (universe.Count < 2)
        {
            _log.LogWarning("CorrelationRefresher: universe size {Size} — skipping refresh.", universe.Count);
            return;
        }

        var fromUtc = asOf.AddDays(-opts.CorrelationLookbackDays);
        var returnsBySymbol = await BuildReturnsAsync(universe, fromUtc, asOf, cancellationToken).ConfigureAwait(false);

        // Build the pairs — upper triangle only (SymbolIdA <= SymbolIdB).
        var ordered = universe.OrderBy(s => s.SymbolId).ToList();
        var pairs = new List<CorrelationPair>(capacity: ordered.Count * (ordered.Count - 1) / 2);
        var indexPairs = new List<(int I, int J, decimal Corr)>(pairs.Capacity);

        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            if (!returnsBySymbol.TryGetValue(a.SymbolId, out var ra) || ra.Length < MinSamples) continue;

            for (var j = i + 1; j < ordered.Count; j++)
            {
                var b = ordered[j];
                if (!returnsBySymbol.TryGetValue(b.SymbolId, out var rb) || rb.Length < MinSamples) continue;

                var n = Math.Min(ra.Length, rb.Length);
                if (n < MinSamples) continue;

                var truncA = ra.Length == n ? ra : ra.AsSpan(ra.Length - n).ToArray();
                var truncB = rb.Length == n ? rb : rb.AsSpan(rb.Length - n).ToArray();

                var r = CorrelationMath.Pearson(truncA, truncB);
                pairs.Add(new CorrelationPair
                {
                    AsOf         = asOf,
                    SymbolIdA    = a.SymbolId,
                    SymbolIdB    = b.SymbolId,
                    LookbackDays = opts.CorrelationLookbackDays,
                    Correlation  = r,
                    SampleCount  = n,
                });
                indexPairs.Add((i, j, r));
            }
        }

        // Cluster via greedy single-link at the configured threshold.
        var labels = CorrelationMath.AssignClusters(ordered.Count, indexPairs, opts.CorrelationThreshold);
        var clusters = new List<CorrelationCluster>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            clusters.Add(new CorrelationCluster
            {
                AsOf      = asOf,
                SymbolId  = ordered[i].SymbolId,
                Cluster   = labels[i],
                Threshold = opts.CorrelationThreshold,
            });
        }

        _log.LogInformation(
            "CorrelationRefresher asOf={AsOf:o}: universe={U} pairs={P} clusters={C} threshold={Thr}",
            asOf, ordered.Count, pairs.Count,
            clusters.GroupBy(c => c.Cluster).Count(),
            opts.CorrelationThreshold);

        await _correlations.ReplaceSnapshotAsync(asOf, pairs, clusters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Symbol>> ResolveUniverseAsync(RiskOptions opts, CancellationToken ct)
    {
        if (opts.CorrelationUniverse is { Count: > 0 } codes)
        {
            // Universe given explicitly — resolve each code to a Symbol row.
            var resolved = new List<Symbol>(codes.Count);
            foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Try spot first, fall back to UMFUT — same SymbolId space is fine.
                var s = await _symbols.GetByExchangeAndCodeAsync(Exchanges.BinanceSpot, code, ct).ConfigureAwait(false)
                     ?? await _symbols.GetByExchangeAndCodeAsync(Exchanges.BinanceUmFut, code, ct).ConfigureAwait(false);
                if (s is not null) resolved.Add(s);
            }
            return resolved;
        }

        return await _symbols.GetActiveAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, decimal[]>> BuildReturnsAsync(
        IReadOnlyList<Symbol> symbols,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var dict = new Dictionary<int, decimal[]>(symbols.Count);
        foreach (var s in symbols)
        {
            try
            {
                var bars = await _candles
                    .GetRangeAsync(s.SymbolId, CandleIntervals.OneDay, fromUtc, toUtc, ct)
                    .ConfigureAwait(false);
                if (bars.Count < MinSamples + 1) continue;

                var closes = new decimal[bars.Count];
                // Returns from GetRangeAsync are open-ascending by convention.
                for (var i = 0; i < bars.Count; i++) closes[i] = bars[i].Close;
                dict[s.SymbolId] = CorrelationMath.SimpleReturns(closes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "CorrelationRefresher: failed to load 1d closes for {Symbol}; skipping.", s.SymbolCode);
            }
        }
        return dict;
    }
}
