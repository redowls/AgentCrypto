namespace TradingBot.Backtest.Domain;

// Persistence codes for dbo.BacktestRuns.RunKind. Kept as plain constants
// so SQL queries and the run-history grid can match against literal strings.
public static class RunKinds
{
    public const string Run         = "RUN";
    public const string Wfa         = "WFA";          // parent fold-orchestrator row
    public const string WfaIs       = "WFA_IS";       // in-sample child fold
    public const string WfaOos      = "WFA_OOS";      // out-of-sample child fold
    public const string McReshuffle = "MC_RESHUFFLE"; // Monte Carlo trade-reshuffle
    public const string McSkip      = "MC_SKIP";      // Monte Carlo trade-skip stress
}

public static class RunStatuses
{
    public const string Pending   = "PENDING";
    public const string Running   = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed    = "FAILED";
}

public static class SimulationKinds
{
    public const string Reshuffle = "RESHUFFLE";
    public const string Skip      = "SKIP";
}
