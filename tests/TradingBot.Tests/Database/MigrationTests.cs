using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Data.Migrations;
using Xunit;

namespace TradingBot.Tests.Database;

[Collection(SqlServerCollection.Name)]
public sealed class MigrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Migrations_create_partition_function_and_scheme()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        var pf = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT name FROM sys.partition_functions WHERE name = N'pf_CandleMonth';");
        var ps = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT name FROM sys.partition_schemes WHERE name = N'ps_CandleMonth';");

        pf.Should().Be("pf_CandleMonth");
        ps.Should().Be("ps_CandleMonth");

        // 13 boundaries → 14 partitions.
        var partitionCount = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM   sys.partition_range_values v
            JOIN   sys.partition_functions pf ON pf.function_id = v.function_id
            WHERE  pf.name = N'pf_CandleMonth';
        """);
        partitionCount.Should().Be(13);
    }

    [Fact]
    public async Task Candles_table_is_partitioned_on_OpenTime()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        var partitionedColumn = await conn.QuerySingleOrDefaultAsync<string?>("""
            SELECT c.name
            FROM   sys.indexes i
            JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  i.object_id = OBJECT_ID(N'dbo.Candles')
              AND  i.index_id  = 1
              AND  ic.partition_ordinal = 1;
        """);

        partitionedColumn.Should().Be("OpenTime");
    }

    [Fact]
    public void Migrations_are_idempotent()
    {
        // Run again on the already-migrated DB — DbUp tracks executed scripts in
        // dbo.SchemaVersions, so the second run should be a no-op (0 scripts).
        var migrator = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance);
        var result = migrator.Migrate(fixture.ConnectionString);

        result.Successful.Should().BeTrue();
        result.ScriptsExecuted.Should().Be(0);
    }

    [Fact]
    public async Task Seed_inserted_eight_symbols()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Symbols;");
        count.Should().Be(8);
    }
}
