namespace TradingBot.Backtest.Configuration;

// Parsed `bt run` subcommand arguments — one full backtest specification.
public sealed class BacktestRunOptions
{
    public required string   StrategyCode { get; init; }   // BREAKOUT_DON | MR_BB_VWAP | TREND_EMA_ADX | ALL
    public required string   SymbolCode   { get; init; }   // BTCUSDT, …
    public required DateTime FromUtc      { get; init; }
    public required DateTime ToUtc        { get; init; }
    public string?           Notes        { get; init; }
    public long?             ParentRunId  { get; init; }   // set for WFA folds
    public string            RunKind      { get; init; } = Domain.RunKinds.Run;
}
