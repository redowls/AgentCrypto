using System.ComponentModel.DataAnnotations;

namespace TradingBot.Execution.Configuration;

/// <summary>
/// Strongly-typed §6 execution-engine knobs. Defaults match the design doc's
/// recommended values; everything bound from <c>Execution:*</c> in
/// <c>appsettings.json</c>.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// Default account when the signal does not pin one explicitly. SPOT or UMFUT.
    [RegularExpression("^(SPOT|UMFUT)$")]
    public string DefaultAccountType { get; set; } = "SPOT";

    /// Approved-intent channel capacity (signals waiting for the engine).
    [Range(16, 8192)]
    public int IntentChannelCapacity { get; set; } = 256;

    /// Reconciliation cadence (§6.4 — every 30s by spec).
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// Orders older than this in a non-terminal state are reconciled against
    /// the exchange. The 60s default matches §6.4.
    public TimeSpan NonTerminalAge { get; set; } = TimeSpan.FromSeconds(60);

    /// Drift threshold (% of position size) above which CRITICAL alert fires.
    [Range(0.0, 1.0)]
    public decimal DriftAlertPctOfQty { get; set; } = 0.005m;       // 0.5%

    /// Drift threshold (USD) above which the kill-switch trips.
    [Range(0.01, 100_000.0)]
    public decimal DriftTripUsd { get; set; } = 5.00m;

    /// Trailing-stop multiplier (× ATR) used when the strategy doesn't carry
    /// a per-strategy override. Defaults to 1.5× ATR per §4.4.
    [Range(0.1, 10.0)]
    public decimal DefaultTrailingAtrMultiplier { get; set; } = 1.5m;

    /// Trend strategy: take 50% off at +R multiple. R is the original risk
    /// distance (entry → SL).
    [Range(0.5, 10.0)]
    public decimal TrendPartialTakeRMultiple { get; set; } = 2.0m;

    /// Trend strategy: fraction of position closed when the +R level is hit.
    [Range(0.05, 0.95)]
    public decimal TrendPartialTakeFraction { get; set; } = 0.50m;

    /// Time-stop ladder per strategy. Bars without a +1R move trigger an exit.
    public TimeStopRules TimeStops { get; set; } = new();

    /// When false (default in tests), the bracket placer falls back to the
    /// futures-style emulated bracket on spot. Production keeps native OCO on.
    public bool EnableSpotNativeOco { get; set; } = true;
}

public sealed class TimeStopRules
{
    /// MR_BB_VWAP — 6 bars on 15m TF (= 90 minutes).
    public int MeanReversionBarsWithoutMove { get; set; } = 6;

    /// BREAKOUT_DON — 4 bars on 1h TF.
    public int BreakoutBarsWithoutMove { get; set; } = 4;

    /// TREND_EMA_ADX — 12 bars on 1h TF.
    public int TrendBarsWithoutMove { get; set; } = 12;
}
