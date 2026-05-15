using TradingBot.Backtest.Domain;

namespace TradingBot.Backtest.Metrics;

// Pure metrics math over a frozen equity curve and trade list. No I/O — the
// runner reads bt.AccountSnapshots / bt.TradeHistory once and feeds them in,
// so the same code drives the live `bt run` report and the WFA/MC roll-ups.
public static class MetricsCalculator
{
    // Bars per year for annualisation. Conservative default: 5-minute bars × 24h × 365d.
    // Callers override when the equity curve is sampled differently.
    public const double DefaultBarsPerYear = 105_120;

    public static BacktestMetrics Compute(
        IReadOnlyList<EquityPoint> curve,
        IReadOnlyList<BacktestTrade> trades,
        decimal startingEquity,
        double barsPerYear = DefaultBarsPerYear,
        int trialsForDsr = 1)
    {
        if (curve.Count == 0)
            return BacktestMetrics.Empty(startingEquity);

        var finalEquity = curve[^1].EquityUsd;
        var netPnl      = finalEquity - startingEquity;

        // Returns: log returns between consecutive equity samples.
        var rets = new double[curve.Count - 1];
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = (double)curve[i - 1].EquityUsd;
            var now  = (double)curve[i].EquityUsd;
            rets[i - 1] = prev > 0 && now > 0 ? Math.Log(now / prev) : 0d;
        }

        var (sharpe, sortino) = SharpeAndSortino(rets, barsPerYear);

        // Max drawdown — walk equity series, track running peak.
        decimal peak = curve[0].EquityUsd;
        decimal mdd  = 0m;
        DateTime mddPeakAt = curve[0].TimeUtc, mddTroughAt = curve[0].TimeUtc;
        DateTime curPeakAt = curve[0].TimeUtc;
        foreach (var p in curve)
        {
            if (p.EquityUsd > peak) { peak = p.EquityUsd; curPeakAt = p.TimeUtc; }
            var dd = peak > 0 ? (p.EquityUsd / peak) - 1m : 0m;
            if (dd < mdd)
            {
                mdd = dd;
                mddPeakAt = curPeakAt;
                mddTroughAt = p.TimeUtc;
            }
        }

        // CAGR / Calmar.
        var totalDays = Math.Max(1.0, (curve[^1].TimeUtc - curve[0].TimeUtc).TotalDays);
        var years     = totalDays / 365.0;
        var cagr      = years > 0 && startingEquity > 0
            ? (decimal)Math.Pow((double)(finalEquity / startingEquity), 1.0 / years) - 1m
            : 0m;
        var calmar    = mdd < 0m ? cagr / Math.Abs(mdd) : 0m;

        // Recovery factor.
        var recovery = mdd < 0m ? netPnl / (Math.Abs(mdd) * startingEquity) : 0m;

        // Trade-level stats.
        var (winRate, profitFactor, avgHoldMin) = TradeStats(trades);

        // Deflated Sharpe Ratio — Bailey & López de Prado (2014).
        var dsr = DeflatedSharpe(rets, sharpe, trialsForDsr);

        return new BacktestMetrics(
            FinalEquity:    finalEquity,
            NetPnlUsd:      netPnl,
            CagrPct:        cagr * 100m,
            Sharpe:         sharpe,
            Sortino:        sortino,
            Calmar:         calmar,
            DeflatedSharpe: dsr,
            MaxDrawdownPct: mdd * 100m,
            MddPeakAt:      mddPeakAt,
            MddTroughAt:    mddTroughAt,
            RecoveryFactor: recovery,
            TradeCount:     trades.Count,
            WinRatePct:     winRate * 100m,
            ProfitFactor:   profitFactor,
            AvgHoldMinutes: avgHoldMin,
            PerStrategy:    GroupBreakdown(trades, t => t.Strategy),
            PerRegime:      GroupBreakdown(trades, t => t.Regime ?? "UNKNOWN"));
    }

    private static (decimal sharpe, decimal sortino) SharpeAndSortino(double[] rets, double barsPerYear)
    {
        if (rets.Length < 2) return (0m, 0m);
        var mean = rets.Average();
        var sumSq = 0d;
        var sumDownSq = 0d;
        for (var i = 0; i < rets.Length; i++)
        {
            var d = rets[i] - mean;
            sumSq += d * d;
            if (rets[i] < 0) sumDownSq += rets[i] * rets[i];
        }
        var stdev     = Math.Sqrt(sumSq / (rets.Length - 1));
        var downStdev = Math.Sqrt(sumDownSq / Math.Max(1, rets.Length - 1));
        var ann       = Math.Sqrt(barsPerYear);
        var sharpe    = stdev > 0     ? (mean * barsPerYear) / (stdev * ann)     : 0d;
        var sortino   = downStdev > 0 ? (mean * barsPerYear) / (downStdev * ann) : 0d;
        return ((decimal)sharpe, (decimal)sortino);
    }

    // Bailey & López de Prado (2014). Adjusts the observed Sharpe by:
    //   1. The expected maximum from N trials under the null hypothesis.
    //   2. The non-normality of returns (skew + excess kurtosis).
    // Returned as a raw probability-style ratio in [-∞, +∞]; > 0.95 ≈ "real".
    private static decimal DeflatedSharpe(double[] rets, decimal observedSharpe, int trials)
    {
        if (rets.Length < 30 || trials < 1) return 0m;

        var n  = rets.Length;
        var mean = rets.Average();
        var stdev = Math.Sqrt(rets.Select(r => (r - mean) * (r - mean)).Sum() / (n - 1));
        if (stdev <= 0) return 0m;

        // Sample skew + excess kurtosis.
        double s3 = 0, s4 = 0;
        foreach (var r in rets)
        {
            var z = (r - mean) / stdev;
            s3 += z * z * z;
            s4 += z * z * z * z;
        }
        var skew     = s3 / n;
        var exKurt   = (s4 / n) - 3.0;

        // Expected max Sharpe under null (Bailey 2014, eq. 5).
        const double Euler = 0.5772156649015329;
        var emaxNull = Math.Sqrt(Math.Max(0d, 2.0 * Math.Log(Math.Max(1, trials))))
                       * (1.0 - Euler / Math.Sqrt(2.0 * Math.Max(1, Math.Log(Math.Max(1, trials)))));

        var sr = (double)observedSharpe;
        var num = (sr - emaxNull) * Math.Sqrt(n - 1);
        var den = Math.Sqrt(Math.Max(1e-12, 1.0 - skew * sr + (exKurt / 4.0) * sr * sr));
        var z2  = num / den;

        // Standard normal CDF approximation.
        return (decimal)NormalCdf(z2);
    }

    private static double NormalCdf(double x)
    {
        // Abramowitz & Stegun 26.2.17, max error 7.5e-8.
        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2.0);
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p  = 0.3275911;
        var t   = 1.0 / (1.0 + p * x);
        var y   = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }

    private static (decimal winRate, decimal profitFactor, decimal avgHoldMin) TradeStats(IReadOnlyList<BacktestTrade> trades)
    {
        if (trades.Count == 0) return (0m, 0m, 0m);
        var wins = 0;
        decimal grossWin = 0m, grossLoss = 0m;
        long holdSum = 0;
        foreach (var t in trades)
        {
            if (t.NetPnlUsd > 0) { wins++; grossWin  += t.NetPnlUsd; }
            else                 {          grossLoss += -t.NetPnlUsd; }
            holdSum += t.HoldingMinutes;
        }
        var winRate     = (decimal)wins / trades.Count;
        var profitFact  = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? decimal.MaxValue : 0m);
        var avgHoldMin  = (decimal)holdSum / trades.Count;
        return (winRate, profitFact, avgHoldMin);
    }

    private static IReadOnlyDictionary<string, GroupBreakdown> GroupBreakdown(
        IReadOnlyList<BacktestTrade> trades, Func<BacktestTrade, string> keySelector)
    {
        var dict = new Dictionary<string, GroupBreakdown>(StringComparer.Ordinal);
        foreach (var grp in trades.GroupBy(keySelector, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = grp.ToList();
            var (winRate, pf, avgHold) = TradeStats(list);
            var total = list.Sum(t => t.NetPnlUsd);
            dict[grp.Key] = new GroupBreakdown(grp.Key, list.Count, winRate * 100m, pf, avgHold, total);
        }
        return dict;
    }
}

public sealed record GroupBreakdown(
    string Key,
    int    TradeCount,
    decimal WinRatePct,
    decimal ProfitFactor,
    decimal AvgHoldMinutes,
    decimal NetPnlUsd);

public sealed record BacktestMetrics(
    decimal FinalEquity,
    decimal NetPnlUsd,
    decimal CagrPct,
    decimal Sharpe,
    decimal Sortino,
    decimal Calmar,
    decimal DeflatedSharpe,
    decimal MaxDrawdownPct,
    DateTime MddPeakAt,
    DateTime MddTroughAt,
    decimal RecoveryFactor,
    int     TradeCount,
    decimal WinRatePct,
    decimal ProfitFactor,
    decimal AvgHoldMinutes,
    IReadOnlyDictionary<string, GroupBreakdown> PerStrategy,
    IReadOnlyDictionary<string, GroupBreakdown> PerRegime)
{
    public static BacktestMetrics Empty(decimal startingEquity) => new(
        FinalEquity: startingEquity, NetPnlUsd: 0m, CagrPct: 0m,
        Sharpe: 0m, Sortino: 0m, Calmar: 0m, DeflatedSharpe: 0m,
        MaxDrawdownPct: 0m, MddPeakAt: DateTime.UnixEpoch, MddTroughAt: DateTime.UnixEpoch,
        RecoveryFactor: 0m, TradeCount: 0,
        WinRatePct: 0m, ProfitFactor: 0m, AvgHoldMinutes: 0m,
        PerStrategy: new Dictionary<string, GroupBreakdown>(),
        PerRegime:   new Dictionary<string, GroupBreakdown>());
}
