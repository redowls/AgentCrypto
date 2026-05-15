namespace TradingBot.Backtest.Domain;

// Mirror of one dbo.BacktestRuns row.
public sealed class BacktestRun
{
    public long      BacktestRunId       { get; set; }
    public string    RunKind             { get; set; } = RunKinds.Run;
    public long?     ParentRunId         { get; set; }
    public string    Strategy            { get; set; } = "";
    public string    Symbols             { get; set; } = "";
    public string    AccountType         { get; set; } = "SPOT";
    public DateTime  FromUtc             { get; set; }
    public DateTime  ToUtc               { get; set; }
    public decimal   StartingEquityUsd   { get; set; }
    public long      Seed                { get; set; }
    public string?   ParametersJson      { get; set; }
    public decimal   FeeMakerBps         { get; set; }
    public decimal   FeeTakerBps         { get; set; }
    public string    SlippageModelVersion{ get; set; } = "v1";
    public string    Status              { get; set; } = RunStatuses.Pending;
    public DateTime  StartedAt           { get; set; }
    public DateTime? CompletedAt         { get; set; }
    public long?     DurationMs          { get; set; }
    public long?     BarsReplayed        { get; set; }
    public int?      TradesGenerated     { get; set; }
    public decimal?  FinalEquityUsd      { get; set; }
    public string?   MetricsJson         { get; set; }
    public string?   ErrorMessage        { get; set; }
    public string?   Notes               { get; set; }
}
