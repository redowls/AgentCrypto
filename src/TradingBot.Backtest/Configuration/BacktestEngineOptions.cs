namespace TradingBot.Backtest.Configuration;

// Run-time knobs for the backtest engine. Bound from "Backtest:*" config and
// frozen onto the BacktestRun row at start of each run for full reproducibility.
public sealed class BacktestEngineOptions
{
    public const string SectionName = "Backtest";

    public string AccountType { get; set; } = "SPOT";

    public decimal StartingEquityUsd { get; set; } = 10_000m;

    // Fees in basis points. 0.02% = 2.0 bps.
    public decimal SpotMakerBps { get; set; } = 10m;
    public decimal SpotTakerBps { get; set; } = 10m;
    public decimal UmFutMakerBps { get; set; } = 2m;
    public decimal UmFutTakerBps { get; set; } = 5m;

    // Bid-ask spread for the simulated book, in bps. 1 bp = 0.01%.
    public decimal SimulatedSpreadBps { get; set; } = 2m;

    // RNG seed — drives any randomness in the engine (slippage perturbation,
    // tie-breaks). Bit-exact determinism requires this and a fixed input set.
    public long RandomSeed { get; set; } = 42;

    // Output directory for CSV curves and the markdown/JSON report.
    public string OutputDirectory { get; set; } = "backtest-output";

    // Cap on the per-bar candle context window the engine reads when building
    // MarketContext (matches the live SignalEngine default).
    public int ContextWindowBars { get; set; } = 250;
}
