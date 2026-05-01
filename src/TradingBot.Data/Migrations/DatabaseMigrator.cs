using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace TradingBot.Data.Migrations;

public sealed class DatabaseMigrator(ILogger<DatabaseMigrator> logger)
{
    // Migration scripts are embedded under `TradingBot.Data.Migrations.Scripts.*.sql`
    // (set via the EmbeddedResource LogicalName in the csproj). DbUp tracks executed
    // scripts in dbo.SchemaVersions, so re-runs are no-ops.
    private const string ScriptNamespace = "TradingBot.Data.Migrations.Scripts.";

    public MigrationResult Migrate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        EnsureDatabase.For.SqlDatabase(connectionString);

        var assembly = Assembly.GetExecutingAssembly();
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ScriptNamespace, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        logger.LogInformation(
            "DbUp: discovered {Count} migration scripts: {Scripts}",
            scripts.Length,
            string.Join(", ", scripts.Select(s => s[ScriptNamespace.Length..])));

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(assembly,
                name => name.StartsWith(ScriptNamespace, StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new DbUpLoggerAdapter(logger))
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            logger.LogError(result.Error, "DbUp migration failed at script {Script}", result.ErrorScript?.Name);
            return new MigrationResult(false, 0, result.Error);
        }

        var executed = result.Scripts.Count();
        logger.LogInformation(
            "DbUp migration successful. Scripts executed this run: {Count}",
            executed);

        return new MigrationResult(true, executed, null);
    }

    private sealed class DbUpLoggerAdapter(ILogger inner) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) =>
            inner.LogTrace(format, args);

        public void LogDebug(string format, params object[] args) =>
            inner.LogDebug(format, args);

        public void LogInformation(string format, params object[] args) =>
            inner.LogInformation(format, args);

        public void LogWarning(string format, params object[] args) =>
            inner.LogWarning(format, args);

        public void LogError(string format, params object[] args) =>
            inner.LogError(format, args);

        public void LogError(Exception ex, string format, params object[] args) =>
            inner.LogError(ex, format, args);
    }
}

public readonly record struct MigrationResult(bool Successful, int ScriptsExecuted, Exception? Error);
