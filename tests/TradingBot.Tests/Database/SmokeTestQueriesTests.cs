using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.Database;

// Direct DDL-state assertions matching the SMOKE TEST CHECKLIST so that a
// single `dotnet test` run can satisfy every checkbox without external tooling.
[Collection(SqlServerCollection.Name)]
public sealed class SmokeTestQueriesTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task SchemaVersions_table_records_four_migration_scripts()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);

        var rows = (await conn.QueryAsync<(string ScriptName, DateTime Applied)>("""
            SELECT ScriptName, Applied
            FROM   dbo.SchemaVersions
            ORDER BY Applied;
        """)).ToList();

        foreach (var (name, applied) in rows)
        {
            output.WriteLine($"  {applied:O}  {name}");
        }

        rows.Should().HaveCount(4);
        rows.Select(r => r.ScriptName)
            .Should().BeEquivalentTo(new[]
            {
                "TradingBot.Data.Migrations.Scripts.001_init_schema.sql",
                "TradingBot.Data.Migrations.Scripts.002_partition_function.sql",
                "TradingBot.Data.Migrations.Scripts.003_indexes.sql",
                "TradingBot.Data.Migrations.Scripts.004_seed_symbols.sql",
            });
    }

    [Fact]
    public async Task All_expected_indexes_exist()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);

        var expected = new[]
        {
            ("dbo.Candles",          "IX_Candles_SymbolInterval_Time"),
            ("dbo.Signals",          "IX_Signals_Sym_Time"),
            ("dbo.Signals",          "IX_Signals_Status_CT"),
            ("dbo.Orders",           "IX_Orders_Symbol_Status"),
            ("dbo.Orders",           "IX_Orders_Submitted"),
            ("dbo.Orders",           "UQ_Orders_ClientOrderId"),
            ("dbo.Fills",            "UQ_Fills_Order_Trade"),
            ("dbo.Positions",        "IX_Positions_Status"),
            ("dbo.TradeHistory",     "IX_TH_Strategy_Exit"),
            ("dbo.AccountSnapshots", "IX_Acct_Time"),
            ("dbo.RiskEvents",       "IX_Risk_Type_Time"),
            ("dbo.AiInteractions",   "IX_Ai_Hash"),
        };

        foreach (var (table, indexName) in expected)
        {
            var found = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.indexes WHERE name = @i AND object_id = OBJECT_ID(@t);",
                new { i = indexName, t = table });
            found.Should().Be(1, $"index {indexName} on {table} should exist");
        }
    }

    [Fact]
    public async Task Candles_table_lives_on_partition_scheme()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        var schemeName = await conn.QuerySingleOrDefaultAsync<string?>("""
            SELECT ps.name
            FROM   sys.indexes i
            JOIN   sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
            WHERE  i.object_id = OBJECT_ID(N'dbo.Candles') AND i.index_id = 1;
        """);
        schemeName.Should().Be("ps_CandleMonth");
    }

    [Fact]
    public async Task Decimal_precision_is_38_18_on_price_columns()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);

        var priceCols = (await conn.QueryAsync<(string TableName, string ColumnName, byte Precision, byte Scale)>("""
            SELECT t.name AS TableName, c.name AS ColumnName, c.precision AS Precision, c.scale AS Scale
            FROM   sys.columns c
            JOIN   sys.tables  t ON t.object_id = c.object_id
            JOIN   sys.types   ty ON ty.user_type_id = c.user_type_id
            WHERE  ty.name = 'decimal'
              AND  t.schema_id = SCHEMA_ID('dbo')
              AND  t.name IN ('Candles','Signals','Orders','Fills','Positions','TradeHistory','Symbols')
              AND  c.name IN ('Open','High','Low','Close','Volume','QuoteVolume','TakerBuyBase',
                              'EntryPrice','StopLoss','TakeProfit','AtrValue','ExitPrice',
                              'Quantity','FilledQty','AvgFillPrice','Price','StopPrice','CommissionPaid',
                              'Commission','TickSize','StepSize','MinNotional','AvgEntryPrice','ClosePrice');
        """)).ToList();

        priceCols.Should().NotBeEmpty();
        priceCols.Should().AllSatisfy(c =>
        {
            c.Precision.Should().Be(38, $"{c.TableName}.{c.ColumnName} should be DECIMAL(38,18)");
            c.Scale.Should().Be(18,    $"{c.TableName}.{c.ColumnName} should be DECIMAL(38,18)");
        });
    }
}
