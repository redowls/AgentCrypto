using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingBot.Backtest.Domain;

namespace TradingBot.Backtest.Metrics;

// Writes the equity / drawdown CSVs and the markdown + JSON metrics reports
// to the run's output directory. All paths are deterministic given the run id.
public static class ReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string EnsureRunDirectory(string baseDir, long runId)
    {
        var path = Path.Combine(baseDir, $"run-{runId:D8}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task WriteEquityCurveAsync(string runDir, IReadOnlyList<EquityPoint> curve, CancellationToken ct)
    {
        var path = Path.Combine(runDir, "equity.csv");
        var sb = new StringBuilder();
        sb.Append("time_utc,equity_usd,available_usd,unrealized_pnl_usd,open_positions,gross_exposure_usd,net_exposure_usd,drawdown_pct\n");
        foreach (var p in curve)
        {
            sb.Append(p.TimeUtc.ToString("O", Inv));            sb.Append(',');
            sb.Append(p.EquityUsd.ToString(Inv));               sb.Append(',');
            sb.Append(p.AvailableUsd.ToString(Inv));            sb.Append(',');
            sb.Append(p.UnrealizedPnlUsd.ToString(Inv));        sb.Append(',');
            sb.Append(p.OpenPositions.ToString(Inv));           sb.Append(',');
            sb.Append(p.GrossExposureUsd.ToString(Inv));        sb.Append(',');
            sb.Append(p.NetExposureUsd.ToString(Inv));          sb.Append(',');
            sb.Append(p.DrawdownPct.ToString(Inv));             sb.Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }

    public static async Task WriteDrawdownCurveAsync(string runDir, IReadOnlyList<EquityPoint> curve, CancellationToken ct)
    {
        var path = Path.Combine(runDir, "drawdown.csv");
        var sb = new StringBuilder();
        sb.Append("time_utc,drawdown_pct\n");
        foreach (var p in curve)
        {
            sb.Append(p.TimeUtc.ToString("O", Inv));   sb.Append(',');
            sb.Append(p.DrawdownPct.ToString(Inv));    sb.Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }

    public static async Task WriteMetricsJsonAsync(string runDir, BacktestMetrics m, CancellationToken ct)
    {
        var path = Path.Combine(runDir, "metrics.json");
        var json = JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    public static async Task WriteMarkdownReportAsync(
        string runDir, BacktestRun run, BacktestMetrics m, CancellationToken ct)
    {
        var path = Path.Combine(runDir, "report.md");
        var sb = new StringBuilder();
        sb.AppendLine($"# Backtest Run #{run.BacktestRunId}");
        sb.AppendLine();
        sb.AppendLine("## Configuration");
        sb.AppendLine();
        sb.AppendLine($"- Strategy: `{run.Strategy}`");
        sb.AppendLine($"- Symbols: `{run.Symbols}`");
        sb.AppendLine($"- Account: `{run.AccountType}`");
        sb.AppendLine($"- Window: `{run.FromUtc:O}` → `{run.ToUtc:O}`");
        sb.AppendLine($"- Starting equity: `{run.StartingEquityUsd.ToString("F2", Inv)} USD`");
        sb.AppendLine($"- Seed: `{run.Seed}`");
        sb.AppendLine($"- Maker fee: `{run.FeeMakerBps.ToString(Inv)} bps`  / Taker fee: `{run.FeeTakerBps.ToString(Inv)} bps`");
        sb.AppendLine($"- Slippage model: `{run.SlippageModelVersion}`");
        sb.AppendLine();
        sb.AppendLine("## Headline metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Final equity (USD) | {m.FinalEquity.ToString("F2", Inv)} |");
        sb.AppendLine($"| Net PnL (USD) | {m.NetPnlUsd.ToString("F2", Inv)} |");
        sb.AppendLine($"| CAGR | {m.CagrPct.ToString("F2", Inv)} % |");
        sb.AppendLine($"| Sharpe | {m.Sharpe.ToString("F3", Inv)} |");
        sb.AppendLine($"| Sortino | {m.Sortino.ToString("F3", Inv)} |");
        sb.AppendLine($"| Calmar | {m.Calmar.ToString("F3", Inv)} |");
        sb.AppendLine($"| Deflated Sharpe (prob) | {m.DeflatedSharpe.ToString("F3", Inv)} |");
        sb.AppendLine($"| Max drawdown | {m.MaxDrawdownPct.ToString("F2", Inv)} % |");
        sb.AppendLine($"| MDD peak → trough | {m.MddPeakAt:O} → {m.MddTroughAt:O} |");
        sb.AppendLine($"| Recovery factor | {m.RecoveryFactor.ToString("F2", Inv)} |");
        sb.AppendLine($"| Trades | {m.TradeCount} |");
        sb.AppendLine($"| Win rate | {m.WinRatePct.ToString("F2", Inv)} % |");
        sb.AppendLine($"| Profit factor | {(m.ProfitFactor == decimal.MaxValue ? "∞" : m.ProfitFactor.ToString("F2", Inv))} |");
        sb.AppendLine($"| Avg holding | {m.AvgHoldMinutes.ToString("F1", Inv)} min |");
        sb.AppendLine();
        AppendBreakdown(sb, "Per strategy", m.PerStrategy);
        AppendBreakdown(sb, "Per regime",   m.PerRegime);
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static void AppendBreakdown(StringBuilder sb, string title, IReadOnlyDictionary<string, GroupBreakdown> dict)
    {
        if (dict.Count == 0) return;
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine("| Group | Trades | Win % | Profit factor | Avg hold (min) | Net PnL (USD) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var kv in dict)
        {
            var g = kv.Value;
            sb.AppendLine($"| {g.Key} | {g.TradeCount} | {g.WinRatePct.ToString("F1", Inv)} | "
                + $"{(g.ProfitFactor == decimal.MaxValue ? "∞" : g.ProfitFactor.ToString("F2", Inv))} | "
                + $"{g.AvgHoldMinutes.ToString("F1", Inv)} | {g.NetPnlUsd.ToString("F2", Inv)} |");
        }
        sb.AppendLine();
    }
}
