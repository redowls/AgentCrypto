using Microsoft.Extensions.Logging;
using TradingBot.Backtest.Domain;
using TradingBot.Backtest.Repositories;

namespace TradingBot.Backtest.MonteCarlo;

// Monte Carlo stress over the trade list of a completed `bt run`. Two modes:
//
//   RESHUFFLE: 1,000 random orderings of the trade sequence. Each ordering is
//              played as if drawing trades sequentially from a fresh starting
//              equity. Reports 5 / 50 / 95 percentile of MaxDrawdown.
//
//   SKIP:      100 random "skip 10–15% of trades" sequences (configurable).
//              Models the operational realities of missed signals (network
//              outages, kill-switch trips, etc.). Reports MDD inflation factor
//              (ratio of skip-MDD to baseline-MDD).
//
// Acceptance gate: 95th-pct MDD < 25% in the RESHUFFLE mode.
internal sealed class MonteCarloRunner
{
    private readonly BacktestTradeHistoryRepository _trades;
    private readonly BacktestRunRepository _runs;
    private readonly MonteCarloResultRepository _mcResults;
    private readonly ILogger<MonteCarloRunner> _log;

    public MonteCarloRunner(
        BacktestTradeHistoryRepository trades,
        BacktestRunRepository runs,
        MonteCarloResultRepository mcResults,
        ILogger<MonteCarloRunner> log)
    {
        _trades    = trades;
        _runs      = runs;
        _mcResults = mcResults;
        _log       = log;
    }

    public async Task RunAsync(MonteCarloConfig cfg, CancellationToken ct)
    {
        var parent = await _runs.GetByIdAsync(cfg.ParentRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Parent run #{cfg.ParentRunId} not found");
        var trades = await _trades.GetForRunAsync(cfg.ParentRunId, ct).ConfigureAwait(false);
        if (trades.Count == 0)
        {
            _log.LogWarning("Parent run #{Id} has no trades — Monte Carlo skipped", cfg.ParentRunId);
            return;
        }

        var startEq = parent.StartingEquityUsd;

        // RESHUFFLE simulations.
        for (var i = 0; i < cfg.ReshuffleIterations; i++)
        {
            var rng = new Random((int)((cfg.Seed ^ unchecked((uint)i * 2654435761u)) & 0x7fffffff));
            var permuted = trades.ToArray();
            ShuffleInPlace(permuted, rng);
            var (finalEq, mdd) = EquityWalk(startEq, permuted);
            await _mcResults.InsertAsync(new McResultRow(
                ParentRunId:   cfg.ParentRunId,
                SimulationKind: SimulationKinds.Reshuffle,
                Iteration:     i,
                Seed:          cfg.Seed ^ i,
                SkipFraction:  null,
                FinalEquityUsd: finalEq,
                MaxDrawdownPct: mdd,
                Sharpe:        null,
                TradesUsed:    permuted.Length), ct).ConfigureAwait(false);
        }

        // SKIP simulations.
        for (var i = 0; i < cfg.SkipIterations; i++)
        {
            var rng = new Random((int)((cfg.Seed ^ unchecked((uint)(i * 2654435761u + 7u))) & 0x7fffffff));
            var skipFrac = cfg.SkipFractionMin + (decimal)rng.NextDouble() * (cfg.SkipFractionMax - cfg.SkipFractionMin);
            var subset = trades.Where(_ => (decimal)rng.NextDouble() > skipFrac).ToArray();
            if (subset.Length == 0) subset = trades.Take(1).ToArray();
            var (finalEq, mdd) = EquityWalk(startEq, subset);
            await _mcResults.InsertAsync(new McResultRow(
                ParentRunId:    cfg.ParentRunId,
                SimulationKind: SimulationKinds.Skip,
                Iteration:      i,
                Seed:           cfg.Seed ^ i ^ 0x5A5A5A5A,
                SkipFraction:   skipFrac,
                FinalEquityUsd: finalEq,
                MaxDrawdownPct: mdd,
                Sharpe:         null,
                TradesUsed:     subset.Length), ct).ConfigureAwait(false);
        }

        var (p5, p50, p95) = await _mcResults.GetMddQuantilesAsync(
            cfg.ParentRunId, SimulationKinds.Reshuffle, ct).ConfigureAwait(false);
        var (sp5, sp50, sp95) = await _mcResults.GetMddQuantilesAsync(
            cfg.ParentRunId, SimulationKinds.Skip, ct).ConfigureAwait(false);

        var verdict = Math.Abs(p95) < 25m;

        _log.LogInformation(
            "MC parent #{Id}: reshuffle MDD p5/p50/p95 = {P5:F2}/{P50:F2}/{P95:F2}%, "
            + "skip MDD p5/p50/p95 = {SP5:F2}/{SP50:F2}/{SP95:F2}% — verdict {Verdict}",
            cfg.ParentRunId, p5, p50, p95, sp5, sp50, sp95, verdict ? "ACCEPT (95th < 25%)" : "REJECT");
    }

    private static void ShuffleInPlace<T>(T[] arr, Random rng)
    {
        // Fisher-Yates with a deterministic Random source.
        for (var i = arr.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    // Walk a sequence of trades through an equity simulator, returning final
    // equity and the worst peak-to-trough drawdown (as a negative percentage).
    private static (decimal finalEquity, decimal maxDdPct) EquityWalk(decimal start, IReadOnlyList<BacktestTrade> trades)
    {
        var eq   = start;
        var peak = start;
        var mdd  = 0m;
        foreach (var t in trades)
        {
            eq += t.NetPnlUsd;
            if (eq > peak) peak = eq;
            if (peak > 0m)
            {
                var dd = (eq / peak) - 1m;
                if (dd < mdd) mdd = dd;
            }
        }
        return (eq, mdd * 100m);
    }
}

public sealed class MonteCarloConfig
{
    public required long ParentRunId       { get; init; }
    public int   ReshuffleIterations       { get; set; } = 1000;
    public int   SkipIterations            { get; set; } = 100;
    public decimal SkipFractionMin         { get; set; } = 0.10m;
    public decimal SkipFractionMax         { get; set; } = 0.15m;
    public long  Seed                      { get; set; } = 42;
}
