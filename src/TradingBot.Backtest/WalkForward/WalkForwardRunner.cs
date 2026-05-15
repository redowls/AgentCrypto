using Microsoft.Extensions.Logging;
using TradingBot.Backtest.Configuration;
using TradingBot.Backtest.Domain;
using TradingBot.Backtest.Engine;
using TradingBot.Backtest.Metrics;
using TradingBot.Backtest.Repositories;

namespace TradingBot.Backtest.WalkForward;

// Walk-forward analysis: rolling IS/OOS folds.
//
//   Window:  [overall_from .. overall_to]
//   Stride:  IS = X months, OOS = Y months, Step = Z months.
//   For each fold f at offset f*Step:
//     IS  = [from + f*Step,             from + f*Step + X]
//     OOS = [from + f*Step + X,         from + f*Step + X + Y]
//   The objective on IS is "maximise Sharpe" (per user choice — see CLAUDE.md
//   memory context). We run the live BacktestEngine on each fold; the
//   parameter grid is materialised by the caller via ParametersJson on the
//   BacktestRun row.
//
//   Acceptance gate: OOS Sharpe ≥ 0.6 × IS Sharpe in ≥ 70% of folds.
//
// §10 v1: this runner currently runs a single parameter set per fold (no
// grid optimisation) and records the Sharpe on each side. Grid search is a
// follow-up — the schema + verdict logic is in place so that lands cleanly.
internal sealed class WalkForwardRunner
{
    private readonly BacktestEngine _engine;
    private readonly BacktestRunRepository _runs;
    private readonly WalkForwardFoldRepository _folds;
    private readonly BacktestTradeHistoryRepository _trades;
    private readonly BacktestAccountSnapshotRepository _snapshots;
    private readonly ILogger<WalkForwardRunner> _log;

    public WalkForwardRunner(
        BacktestEngine engine,
        BacktestRunRepository runs,
        WalkForwardFoldRepository folds,
        BacktestTradeHistoryRepository trades,
        BacktestAccountSnapshotRepository snapshots,
        ILogger<WalkForwardRunner> log)
    {
        _engine    = engine;
        _runs      = runs;
        _folds     = folds;
        _trades    = trades;
        _snapshots = snapshots;
        _log       = log;
    }

    public async Task<long> RunAsync(WalkForwardConfig cfg, CancellationToken ct)
    {
        // Insert parent WFA run (header).
        var parent = new BacktestRun
        {
            RunKind             = RunKinds.Wfa,
            Strategy            = cfg.StrategyCode,
            Symbols             = cfg.SymbolCode,
            AccountType         = cfg.AccountType,
            FromUtc             = cfg.FromUtc,
            ToUtc               = cfg.ToUtc,
            StartingEquityUsd   = cfg.StartingEquityUsd,
            Seed                = cfg.Seed,
            FeeMakerBps         = cfg.FeeMakerBps,
            FeeTakerBps         = cfg.FeeTakerBps,
            SlippageModelVersion = "v1",
            Status              = RunStatuses.Running,
            StartedAt           = DateTime.UtcNow,
            Notes               = $"WFA IS={cfg.InSampleMonths}m OOS={cfg.OutOfSampleMonths}m Step={cfg.StepMonths}m",
        };
        var parentId = await _runs.InsertAsync(parent, ct).ConfigureAwait(false);

        var folds = EnumerateFolds(cfg).ToList();
        var foldVerdicts = new List<bool>();

        for (var i = 0; i < folds.Count; i++)
        {
            var f = folds[i];
            _log.LogInformation("WFA fold #{Idx}: IS [{IsFrom:O}..{IsTo:O}], OOS [{OosFrom:O}..{OosTo:O}]",
                i, f.IsFrom, f.IsTo, f.OosFrom, f.OosTo);

            var isRunId  = await _engine.RunAsync(new BacktestRunOptions
            {
                StrategyCode = cfg.StrategyCode,
                SymbolCode   = cfg.SymbolCode,
                FromUtc      = f.IsFrom,
                ToUtc        = f.IsTo,
                ParentRunId  = parentId,
                RunKind      = RunKinds.WfaIs,
                Notes        = $"WFA-IS#{i}",
            }, ct).ConfigureAwait(false);

            var oosRunId = await _engine.RunAsync(new BacktestRunOptions
            {
                StrategyCode = cfg.StrategyCode,
                SymbolCode   = cfg.SymbolCode,
                FromUtc      = f.OosFrom,
                ToUtc        = f.OosTo,
                ParentRunId  = parentId,
                RunKind      = RunKinds.WfaOos,
                Notes        = $"WFA-OOS#{i}",
            }, ct).ConfigureAwait(false);

            var foldId = await _folds.InsertAsync(new WfaFoldRow(
                ParentRunId: parentId, FoldIndex: i,
                IsFromUtc: f.IsFrom, IsToUtc: f.IsTo,
                OosFromUtc: f.OosFrom, OosToUtc: f.OosTo,
                IsRunId: isRunId, OosRunId: oosRunId,
                BestParametersJson: null), ct).ConfigureAwait(false);

            // Pull each child's metrics blob and parse the Sharpe.
            var (isSharpe, isCalmar, isMdd, isCount) = await ReadMetricsAsync(isRunId, ct).ConfigureAwait(false);
            var (oosSharpe, oosCalmar, oosMdd, oosCount) = await ReadMetricsAsync(oosRunId, ct).ConfigureAwait(false);

            // Acceptance: OOS Sharpe ≥ 0.6 × IS Sharpe.
            var pass = isSharpe > 0m && oosSharpe >= 0.6m * isSharpe;
            foldVerdicts.Add(pass);

            await _folds.UpdateMetricsAsync(new WfaFoldMetricsUpdate(
                WfaFoldId: foldId,
                IsSharpe: isSharpe, OosSharpe: oosSharpe,
                IsCalmar: isCalmar, OosCalmar: oosCalmar,
                IsMaxDdPct: isMdd, OosMaxDdPct: oosMdd,
                IsTradeCount: isCount, OosTradeCount: oosCount,
                AcceptanceMet: pass), ct).ConfigureAwait(false);
        }

        var passes  = foldVerdicts.Count(v => v);
        var passPct = folds.Count == 0 ? 0d : (double)passes / folds.Count;
        var verdict = passPct >= 0.70;

        _log.LogInformation("WFA run #{ParentId} verdict: {Passes}/{Total} folds passed ({Pct:P0}) → {Verdict}",
            parentId, passes, folds.Count, passPct, verdict ? "ACCEPT" : "REJECT");

        await _runs.FinalizeAsync(parentId, RunStatuses.Completed, DateTime.UtcNow,
            durationMs: 0, barsReplayed: null, tradesGenerated: null, finalEquityUsd: null,
            metricsJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                folds          = folds.Count,
                folds_passed   = passes,
                folds_pass_pct = passPct,
                verdict        = verdict ? "ACCEPT" : "REJECT",
            }),
            errorMessage: null, ct).ConfigureAwait(false);

        return parentId;
    }

    private async Task<(decimal sharpe, decimal calmar, decimal mdd, int count)> ReadMetricsAsync(
        long childRunId, CancellationToken ct)
    {
        var run = await _runs.GetByIdAsync(childRunId, ct).ConfigureAwait(false);
        if (run is null || string.IsNullOrEmpty(run.MetricsJson)) return (0m, 0m, 0m, 0);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(run.MetricsJson);
            var root = doc.RootElement;
            return (
                sharpe: root.GetProperty("Sharpe").GetDecimal(),
                calmar: root.GetProperty("Calmar").GetDecimal(),
                mdd:    root.GetProperty("MaxDrawdownPct").GetDecimal(),
                count:  root.GetProperty("TradeCount").GetInt32());
        }
        catch
        {
            return (0m, 0m, 0m, 0);
        }
    }

    private static IEnumerable<FoldWindow> EnumerateFolds(WalkForwardConfig cfg)
    {
        var cursor = cfg.FromUtc;
        var idx = 0;
        while (true)
        {
            var isFrom  = cursor;
            var isTo    = isFrom.AddMonths(cfg.InSampleMonths);
            var oosFrom = isTo;
            var oosTo   = oosFrom.AddMonths(cfg.OutOfSampleMonths);
            if (oosTo > cfg.ToUtc) yield break;
            yield return new FoldWindow(idx++, isFrom, isTo, oosFrom, oosTo);
            cursor = cursor.AddMonths(cfg.StepMonths);
        }
    }

    private sealed record FoldWindow(int Index, DateTime IsFrom, DateTime IsTo, DateTime OosFrom, DateTime OosTo);
}

public sealed class WalkForwardConfig
{
    public string StrategyCode      { get; set; } = "";
    public string SymbolCode        { get; set; } = "";
    public string AccountType       { get; set; } = "SPOT";
    public DateTime FromUtc         { get; set; }
    public DateTime ToUtc           { get; set; }
    public int InSampleMonths       { get; set; } = 6;
    public int OutOfSampleMonths    { get; set; } = 1;
    public int StepMonths           { get; set; } = 1;
    public decimal StartingEquityUsd { get; set; } = 10_000m;
    public long Seed                { get; set; } = 42;
    public decimal FeeMakerBps      { get; set; } = 10m;
    public decimal FeeTakerBps      { get; set; } = 10m;

    // §10 v2: parameter grid for per-fold optimisation. v1 uses defaults.
    public Dictionary<string, decimal[]>? ParameterGrid { get; set; }
}
