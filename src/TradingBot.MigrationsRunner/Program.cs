using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TradingBot.Data.Migrations;

namespace TradingBot.MigrationsRunner;

// Standalone entry-point for DbUp migrations.
//
// Connection string resolved from (priority order):
//   1. --connection "<conn>"
//   2. ConnectionStrings:TradingDb env / config
//   3. Database:ConnectionString env / config
//
// Flags:
//   --reset   Drop & recreate the target database before migrating.
//             Required for Make-DevDb.ps1's idempotent re-run guarantee.
//
// Exit codes: 0 success / 1 failure / 2 misconfiguration.
internal static class Program
{
    public static int Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["--connection"] = "Database:ConnectionString",
                ["-c"]           = "Database:ConnectionString",
            })
            .Build();

        var connectionString =
            config["Database:ConnectionString"]
            ?? config["ConnectionStrings:TradingDb"];

        var reset = args.Any(a => string.Equals(a, "--reset", StringComparison.OrdinalIgnoreCase));

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(opt =>
            {
                opt.SingleLine      = true;
                opt.TimestampFormat = "HH:mm:ss ";
            }));
        var logger = loggerFactory.CreateLogger("TradingBot.MigrationsRunner");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError(
                "No connection string provided. Pass --connection \"<conn>\", " +
                "or set Database__ConnectionString / ConnectionStrings__TradingDb.");
            return 2;
        }

        try
        {
            if (reset)
            {
                ResetDatabase(connectionString, logger);
            }

            var migrator = new DatabaseMigrator(loggerFactory.CreateLogger<DatabaseMigrator>());
            var result = migrator.Migrate(connectionString);
            return result.Successful ? 0 : 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Migration runner crashed");
            return 1;
        }
    }

    // Drops and recreates the target database. Connects to `master`, then
    // forcibly disconnects existing sessions and drops/recreates by name.
    private static void ResetDatabase(string connectionString, ILogger logger)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var dbName = b.InitialCatalog;
        if (string.IsNullOrWhiteSpace(dbName))
        {
            throw new InvalidOperationException(
                "Connection string must include 'Initial Catalog' / 'Database' to use --reset.");
        }

        b.InitialCatalog = "master";
        var masterConn = b.ConnectionString;

        using var conn = new SqlConnection(masterConn);
        conn.Open();

        logger.LogInformation("Resetting database {Database}…", dbName);

        // Quote identifier safely.
        var quoted = "[" + dbName.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var sql = $"""
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quoted};
            END
            CREATE DATABASE {quoted};
        """;

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.ExecuteNonQuery();

        logger.LogInformation("Database {Database} reset.", dbName);
    }
}
