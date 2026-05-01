using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Data.Migrations;
using TradingBot.Worker.Configuration;

namespace TradingBot.Worker.HostedServices;

// Runs DbUp on startup. Registered with priority before any data-touching
// hosted services so the schema is in place before they fire.
//
// Failures are fatal: we throw, the host shuts down, and the operator sees the
// migration error in logs. Running with a half-migrated DB would be worse.
internal sealed class DatabaseMigrationStartupService(
    DatabaseMigrator migrator,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrationStartupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;

        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            logger.LogInformation("Database connection string not configured; skipping migrations.");
            return Task.CompletedTask;
        }

        if (!opts.RunMigrationsOnStartup)
        {
            logger.LogInformation("Database:RunMigrationsOnStartup=false; skipping migrations.");
            return Task.CompletedTask;
        }

        logger.LogInformation("Running database migrations…");
        var result = migrator.Migrate(opts.ConnectionString);

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                "Database migration failed during startup; aborting host.",
                result.Error);
        }

        logger.LogInformation("Database migrations complete ({Count} new scripts).", result.ScriptsExecuted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
