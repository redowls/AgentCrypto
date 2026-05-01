using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using TradingBot.Core.Domain;
using TradingBot.Data.Connection;
using TradingBot.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace TradingBot.Tests.Database;

[Collection(SqlServerCollection.Name)]
public sealed class BulkInsertBenchmarkTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private const int RowCount   = 25_000;
    private const int MinRowsSec = 10_000;

    [Fact]
    public async Task SqlBulkCopy_meets_10k_rows_per_second_target()
    {
        var cf       = new SqlConnectionFactory(fixture.ConnectionString);
        var repo     = new CandleRepository(cf);
        var symbolId = await ResolveBtcSymbolIdAsync();

        var candles = BuildCandles(symbolId, RowCount).ToList();

        // Cold-start the connection pool so the first measured run isn't paying
        // for SqlClient warm-up.
        await using (var warm = new SqlConnection(fixture.ConnectionString))
        {
            await warm.OpenAsync();
        }

        var sw = Stopwatch.StartNew();
        var affected = await repo.BulkUpsertAsync(candles, default);
        sw.Stop();

        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var rate    = candles.Count / seconds;

        output.WriteLine($"Bulk upsert: {candles.Count:N0} rows in {sw.ElapsedMilliseconds:N0} ms " +
                         $"= {rate:N0} rows/sec (target ≥ {MinRowsSec:N0}). MERGE affected={affected}.");

        affected.Should().BeGreaterOrEqualTo(candles.Count);
        rate.Should().BeGreaterOrEqualTo(MinRowsSec,
            "bulk insert performance target from §2 is 10K rows/sec on dev hardware");
    }

    private async Task<int> ResolveBtcSymbolIdAsync()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT SymbolId FROM dbo.Symbols WHERE Exchange='BINANCE_SPOT' AND Symbol='BTCUSDT';");
    }

    private static IEnumerable<Candle> BuildCandles(int symbolId, int count)
    {
        // 5-minute candles starting 2026-05-01 — every row goes into the May
        // partition so we exercise the partitioned write path. Each row has a
        // unique OpenTime, so this is purely an INSERT workload through the
        // MERGE drain.
        var t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var open = t0.AddMinutes(5 * i);
            yield return new Candle
            {
                SymbolId     = symbolId,
                Interval     = "5m",
                OpenTime     = open,
                CloseTime    = open.AddMinutes(4).AddSeconds(59),
                Open         = 60_000m + i,
                High         = 60_100m + i,
                Low          = 59_900m + i,
                Close        = 60_050m + i,
                Volume       = 12.34m,
                QuoteVolume  = 740_000m,
                TradeCount   = 100 + i,
                TakerBuyBase = 6.17m,
                IsClosed     = true,
            };
        }
    }
}
