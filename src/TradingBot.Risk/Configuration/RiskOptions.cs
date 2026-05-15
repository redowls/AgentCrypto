using System.ComponentModel.DataAnnotations;

namespace TradingBot.Risk.Configuration;

/// <summary>
/// Strongly-typed §8 risk parameters. Every threshold lives here so a config
/// review can see every magic number in one place and risk hardening is a
/// pull-request, never a code edit.
/// </summary>
public sealed class RiskOptions
{
    public const string SectionName = "Risk";

    /// Risk fraction per trade — §8.1 default 1%.
    [Range(0.0001, 0.10)]
    public decimal RiskPerTradeFraction { get; set; } = 0.01m;

    /// §8.2 daily loss limit — fires when DailyPnlPct ≤ -DailyLossLimitPct.
    [Range(-0.20, -0.001)]
    public decimal DailyLossLimitPct { get; set; } = -0.03m;

    /// §8.2 hard halt drawdown — fires when DrawdownPct ≤ -MaxDrawdownHaltPct.
    [Range(-0.50, -0.001)]
    public decimal MaxDrawdownHaltPct { get; set; } = -0.15m;

    /// §8.2 max concurrent positions across all strategies + accounts.
    [Range(1, 16)]
    public int MaxConcurrentPositions { get; set; } = 4;

    /// §8.2 single-symbol cap as a fraction of equity.
    [Range(0.05, 1.00)]
    public decimal SingleSymbolCapFraction { get; set; } = 0.50m;

    /// §8.2 gross exposure cap as a multiple of equity (futures default 2×).
    [Range(0.10, 5.00)]
    public decimal GrossExposureCapMultiple { get; set; } = 2.00m;

    /// §8.4 ladder thresholds — DD bracket inclusive lower bound → multiplier.
    /// Defaults match the design doc exactly. Sorted descending by threshold.
    public List<DrawdownLadderRung> DrawdownLadder { get; set; } = new()
    {
        new(-0.05m, 1.00m),
        new(-0.10m, 0.50m),
        new(-0.15m, 0.25m),
    };

    /// §8.1 vol-adjust thresholds (ATR / SMA(ATR,50) ratio).
    [Range(0.5, 3.0)]
    public decimal VolAdjustHighRatio { get; set; } = 1.4m;
    [Range(0.1, 1.5)]
    public decimal VolAdjustLowRatio  { get; set; } = 0.7m;
    [Range(0.1, 2.0)]
    public decimal VolAdjustHighFactor { get; set; } = 0.7m;
    [Range(0.5, 3.0)]
    public decimal VolAdjustLowFactor  { get; set; } = 1.2m;
    [Range(0.1, 2.0)]
    public decimal VolAdjustDefault    { get; set; } = 1.0m;

    /// §8.3 cluster correlation threshold for the greedy partition.
    [Range(0.0, 1.0)]
    public decimal CorrelationThreshold { get; set; } = 0.70m;

    /// §8.3 nightly job: lookback for return correlation. Default 30 days.
    [Range(7, 365)]
    public int CorrelationLookbackDays { get; set; } = 30;

    /// §8.2 — funding-rate veto threshold. Skip futures entries when
    /// |upcoming funding| &gt; this AND we'd be on the paying side.
    [Range(0.0, 0.01)]
    public decimal FundingVetoAbsThreshold { get; set; } = 0.0005m; // 0.05%

    /// §8.2 — only veto when the next funding tick is closer than this. The
    /// design doc treats funding as relevant on entries near the tick; a
    /// trade entered ~hours before the tick has time to convert into PnL
    /// dwarfing the fee. Default 30 minutes.
    public TimeSpan FundingVetoWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// Reconciliation drift trip threshold, in USD (§8.5 / §6.4).
    [Range(0.01, 1000.0)]
    public decimal ReconciliationDriftTripUsd { get; set; } = 5.00m;

    /// Account snapshot persistence cadence (§9 schedule). Default 1 minute.
    public TimeSpan AccountSnapshotInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// Cron expression for the §8.3 nightly correlation refresh. Default
    /// 02:00 UTC daily, matching the §9 ops schedule.
    [Required]
    public string CorrelationRefreshCron { get; set; } = "0 0 2 * * ?";

    /// Symbols included in the correlation universe. Empty = use every active
    /// symbol in dbo.Symbols.
    public List<string> CorrelationUniverse { get; set; } = new();

    /// When true, a missing or stale correlation snapshot fails OPEN — the
    /// gate does not reject; only the other gates apply. Set false to fail
    /// closed in production once the nightly job has run successfully once.
    public bool AllowCorrelationGateBypassWhenStale { get; set; } = true;
}

/// <param name="DrawdownAtOrAbove">Inclusive lower bound for the rung. A
/// state with <c>DrawdownPct</c> ≥ this value (i.e. closer to zero) qualifies.
/// Rungs are evaluated top-down; the first match wins.</param>
/// <param name="Multiplier">Risk multiplier applied to the base 1% per-trade
/// risk. 0 means "halt all entries" and is paired with the §8.2 -15% gate.</param>
public sealed record DrawdownLadderRung(decimal DrawdownAtOrAbove, decimal Multiplier);
