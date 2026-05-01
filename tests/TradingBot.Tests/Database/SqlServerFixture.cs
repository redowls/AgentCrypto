using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;
using TradingBot.Data.Migrations;
using Xunit;

namespace TradingBot.Tests.Database;

// Two modes:
//   • Default: spin up a SQL Server 2022 Testcontainer and run DbUp against it.
//   • External: when env var INTEGRATION_DB_CONNECTION_STRING is set (e.g. when
//     a developer points the suite at a live local DB), skip the container and
//     migrate that DB instead. Useful in environments without Docker.
//
// Either way, after InitializeAsync the fixture exposes a connection string
// to a fully-migrated DB.
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string ExternalConnEnvVar = "INTEGRATION_DB_CONNECTION_STRING";

    private readonly string? _externalConn = Environment.GetEnvironmentVariable(ExternalConnEnvVar);
    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_externalConn))
        {
            ConnectionString = _externalConn;
        }
        else
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("Strong!Passw0rd_2026")
                .Build();

            try
            {
                await _container.StartAsync();
            }
            catch (Exception ex)
            {
                throw new SkipException(
                    "Integration tests require either Docker (Testcontainers) or " +
                    $"the {ExternalConnEnvVar} env var. Container start failed: {ex.Message}");
            }

            ConnectionString = _container.GetConnectionString()
                + ";TrustServerCertificate=True;Database=TradingDb;";
        }

        var migrator = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance);
        var result = migrator.Migrate(ConnectionString);

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                "Initial migration failed during fixture init", result.Error);
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "IntegrationDb";
}

// Thrown by the fixture when neither Docker nor an external DB is available.
public sealed class SkipException(string message) : Exception(message);
