using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Connection;
using TradingBot.Data.Repositories;
using Xunit;

namespace TradingBot.Tests.Database;

[Collection(SqlServerCollection.Name)]
public sealed class RepositoryTests(SqlServerFixture fixture)
{
    private IDbConnectionFactory Cf => new SqlConnectionFactory(fixture.ConnectionString);

    private async Task<int> ResolveBtcSymbolIdAsync()
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT SymbolId FROM dbo.Symbols WHERE Exchange='BINANCE_SPOT' AND Symbol='BTCUSDT';");
    }

    [Fact]
    public async Task Candle_upsert_then_read_round_trips()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var repo = new CandleRepository(Cf);

        var openTime = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var candle = NewCandle(symbolId, openTime, close: 70_000m);

        var first = await repo.UpsertAsync(candle, default);
        first.Should().Be(1);

        // Re-upsert with a different close — should update, not duplicate.
        candle.Close = 70_123.45m;
        candle.IsClosed = true;
        var second = await repo.UpsertAsync(candle, default);
        second.Should().Be(1);

        var read = await repo.GetAsync(symbolId, "1h", openTime, default);
        read.Should().NotBeNull();
        read!.Close.Should().Be(70_123.45m);
        read.IsClosed.Should().BeTrue();
        read.OpenTime.Kind.Should().Be(DateTimeKind.Utc);

        var range = await repo.GetRangeAsync(symbolId, "1h",
            openTime.AddMinutes(-1), openTime.AddMinutes(60), default);
        range.Should().HaveCount(1);
    }

    [Fact]
    public async Task Signal_insert_then_status_update()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var repo = new SignalRepository(Cf);

        var signal = new Signal
        {
            SymbolId    = symbolId,
            Strategy    = "BREAKOUT_DON",
            Interval    = "1h",
            BarOpenTime = new DateTime(2026, 4, 1, 13, 0, 0, DateTimeKind.Utc),
            Side        = Sides.Buy,
            EntryPrice  = 70_000m,
            StopLoss    = 68_000m,
            TakeProfit  = 74_000m,
            Confidence  = 0.72m,
            Status      = SignalStatuses.Generated,
        };

        var id = await repo.InsertAsync(signal, default);
        id.Should().BeGreaterThan(0);

        var rows = await repo.UpdateStatusAsync(id, SignalStatuses.Approved, "ok", default);
        rows.Should().Be(1);

        var read = await repo.GetByIdAsync(id, default);
        read!.Status.Should().Be(SignalStatuses.Approved);
        read.Reason.Should().Be("ok");
    }

    [Fact]
    public async Task Order_unique_ClientOrderId_constraint_enforced()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var repo = new OrderRepository(Cf);

        var clientId = Guid.NewGuid().ToString();
        var first = NewOrder(symbolId, clientId);

        var id1 = await repo.InsertIfNewAsync(first, default);

        // Re-insert via repo: idempotent path returns the same id.
        var second = NewOrder(symbolId, clientId);
        var id2 = await repo.InsertIfNewAsync(second, default);
        id2.Should().Be(id1);

        // Direct INSERT bypassing the repo idempotency check must fail with a
        // unique-constraint violation (proves the DB constraint is doing its job).
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        Func<Task> directInsert = () => conn.ExecuteAsync("""
            INSERT INTO dbo.Orders
                (SymbolId, AccountType, ClientOrderId, OrderType, Side, Quantity, Status)
            VALUES
                (@SymbolId, 'SPOT', @ClientOrderId, 'LIMIT', 'BUY', 0.001, 'NEW');
        """, new { SymbolId = symbolId, ClientOrderId = clientId });

        await directInsert.Should().ThrowAsync<SqlException>()
            .Where(e => e.Number == 2627 || e.Number == 2601); // unique key / index violation
    }

    [Fact]
    public async Task Order_status_update_round_trips()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var repo = new OrderRepository(Cf);

        var order = NewOrder(symbolId, Guid.NewGuid().ToString());
        var id = await repo.InsertIfNewAsync(order, default);

        var updated = await repo.UpdateStatusAsync(
            id, OrderStatuses.Filled, filledQty: 0.001m, avgFillPrice: 70_010m,
            commissionPaid: 0.07m, commissionAsset: "USDT", default);
        updated.Should().Be(1);

        var read = await repo.GetByIdAsync(id, default);
        read!.Status.Should().Be(OrderStatuses.Filled);
        read.FilledQty.Should().Be(0.001m);
        read.AvgFillPrice.Should().Be(70_010m);
    }

    [Fact]
    public async Task Fill_idempotent_insert_by_OrderId_TradeId()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var orderRepo = new OrderRepository(Cf);
        var fillRepo = new FillRepository(Cf);

        var orderId = await orderRepo.InsertIfNewAsync(NewOrder(symbolId, Guid.NewGuid().ToString()), default);

        var fill = new Fill
        {
            OrderId         = orderId,
            TradeId         = 9_999_001,
            Quantity        = 0.001m,
            Price           = 70_010m,
            Commission      = 0.07m,
            CommissionAsset = "USDT",
            IsMaker         = false,
            TradeTime       = DateTime.UtcNow,
        };
        (await fillRepo.InsertIfNewAsync(fill, default)).Should().BeTrue();
        (await fillRepo.InsertIfNewAsync(fill, default)).Should().BeFalse();

        var fills = await fillRepo.GetByOrderAsync(orderId, default);
        fills.Should().HaveCount(1);
    }

    [Fact]
    public async Task Position_lifecycle_open_to_closed()
    {
        var symbolId = await ResolveBtcSymbolIdAsync();
        var repo = new PositionRepository(Cf);

        var pos = new Position
        {
            SymbolId       = symbolId,
            AccountType    = AccountTypes.Spot,
            Side           = PositionSides.Long,
            Quantity       = 0.001m,
            AvgEntryPrice  = 70_000m,
            StopLoss       = 68_000m,
            TakeProfit     = 74_000m,
            InitialRiskUsd = 2m,
            OpenedAt       = DateTime.UtcNow,
            Status         = PositionStatuses.Open,
        };
        var id = await repo.InsertAsync(pos, default);

        await repo.UpdateStopsAsync(id, stopLoss: 69_000m, takeProfit: 75_000m, default);
        var afterMove = await repo.GetByIdAsync(id, default);
        afterMove!.StopLoss.Should().Be(69_000m);
        afterMove.TakeProfit.Should().Be(75_000m);

        var closedAt = DateTime.UtcNow;
        var closedRows = await repo.CloseAsync(id, closedAt, closePrice: 73_000m,
            realizedPnlUsd: 3m, default);
        closedRows.Should().Be(1);

        var read = await repo.GetByIdAsync(id, default);
        read!.Status.Should().Be(PositionStatuses.Closed);
        read.RealizedPnlUsd.Should().Be(3m);
    }

    private static Candle NewCandle(int symbolId, DateTime openTime, decimal close) => new()
    {
        SymbolId     = symbolId,
        Interval     = "1h",
        OpenTime     = openTime,
        CloseTime    = openTime.AddMinutes(59).AddSeconds(59),
        Open         = 69_900m,
        High         = 70_500m,
        Low          = 69_800m,
        Close        = close,
        Volume       = 123.456m,
        QuoteVolume  = 8_640_000m,
        TradeCount   = 4321,
        TakerBuyBase = 60.5m,
        IsClosed     = false,
    };

    private static Order NewOrder(int symbolId, string clientOrderId) => new()
    {
        SymbolId        = symbolId,
        AccountType     = AccountTypes.Spot,
        ClientOrderId   = clientOrderId,
        OrderType       = OrderTypes.Limit,
        Side            = Sides.Buy,
        Quantity        = 0.001m,
        Price           = 70_000m,
        TimeInForce     = "GTC",
        Status          = OrderStatuses.New,
        FilledQty       = 0m,
        CommissionPaid  = 0m,
    };
}
