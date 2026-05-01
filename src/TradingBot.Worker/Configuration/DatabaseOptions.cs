namespace TradingBot.Worker.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// Connection string for the canonical SQL Server instance. When null/empty,
    /// repositories are not registered and the SQL health check is skipped.
    public string? ConnectionString { get; init; }

    /// When true, the worker runs DbUp migrations during startup (before any
    /// hosted service that touches the DB). Defaults to true in Development;
    /// production deployments typically run TradingBot.MigrationsRunner as an
    /// init container instead and set this to false.
    public bool RunMigrationsOnStartup { get; init; } = true;
}
