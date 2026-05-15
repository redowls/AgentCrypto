using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Configuration;

namespace TradingBot.Risk.Correlation;

/// <summary>
/// Read-side answer to "is this symbol's cluster already occupied on the
/// same side?" — driven by <c>dbo.CorrelationClusters</c> rebuilt nightly.
///
/// Failsafe: when no snapshot exists yet, falls back to permissive (allow
/// the gate to pass) when <see cref="RiskOptions.AllowCorrelationGateBypassWhenStale"/>
/// is true; restrictive (always block) otherwise. Production should leave
/// the bypass on until the first nightly refresh has succeeded, then flip it.
/// </summary>
public sealed class CorrelationService : ICorrelationService
{
    private readonly ICorrelationRepository _repo;
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly ILogger<CorrelationService> _log;

    public CorrelationService(
        ICorrelationRepository repo,
        IOptionsMonitor<RiskOptions> options,
        ILogger<CorrelationService> log)
    {
        _repo = repo;
        _options = options;
        _log = log;
    }

    public async Task<bool> IsClusterOccupiedAsync(
        int symbolId,
        string side,
        IReadOnlyCollection<Position> openPositions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openPositions);

        var asOf = await _repo.GetLatestAsOfAsync(cancellationToken).ConfigureAwait(false);
        if (asOf is null)
        {
            _log.LogDebug(
                "Correlation snapshot empty; gate {Decision} for symbol {Symbol}.",
                _options.CurrentValue.AllowCorrelationGateBypassWhenStale ? "BYPASS" : "BLOCK",
                symbolId);
            return !_options.CurrentValue.AllowCorrelationGateBypassWhenStale;
        }

        var myCluster = await _repo.GetClusterAsync(asOf.Value, symbolId, cancellationToken).ConfigureAwait(false);
        if (myCluster is null)
        {
            // Symbol isn't in the universe — treated as its own cluster, never blocks.
            return false;
        }

        var members = await _repo.GetClusterMembersAsync(asOf.Value, myCluster.Value, cancellationToken).ConfigureAwait(false);

        foreach (var pos in openPositions)
        {
            if (!members.Contains(pos.SymbolId)) continue;
            if (string.Equals(pos.Side, side, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogDebug(
                    "Cluster occupied: incoming {Symbol}/{Side} clashes with open position {OpenSymbol}/{OpenSide} in cluster {Cluster}",
                    symbolId, side, pos.SymbolId, pos.Side, myCluster);
                return true;
            }
        }

        return false;
    }

    /// Minimal helper for tests / non-Quartz callers — exposes the symbol's
    /// current cluster index. Not part of the interface; kept internal-style
    /// public to make property-test access trivial.
    public Task<int?> GetClusterAsync(int symbolId, CancellationToken cancellationToken) =>
        GetClusterCoreAsync(symbolId, cancellationToken);

    private async Task<int?> GetClusterCoreAsync(int symbolId, CancellationToken ct)
    {
        var asOf = await _repo.GetLatestAsOfAsync(ct).ConfigureAwait(false);
        if (asOf is null) return null;
        return await _repo.GetClusterAsync(asOf.Value, symbolId, ct).ConfigureAwait(false);
    }
}
