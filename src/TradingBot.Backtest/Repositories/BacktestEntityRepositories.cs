using Dapper;
using TradingBot.Backtest.Domain;
using TradingBot.Core.Domain;
using TradingBot.Data.Connection;

namespace TradingBot.Backtest.Repositories;

// Per-run repositories targeting the bt.* schema. The live dbo.* repositories
// are deliberately not reused — rebinding their hard-coded "dbo." prefix would
// require touching live code, and the bt.* tables carry an extra BacktestRunId
// column we need on every insert.

internal sealed class BacktestSignalRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestSignalRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(long runId, Signal s, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.Signals
              (BacktestRunId, SymbolId, Strategy, [Interval], BarOpenTime, Side,
               EntryPrice, StopLoss, TakeProfit, AtrValue, Regime, Confidence,
               Status, Reason, CreatedAt)
            OUTPUT INSERTED.SignalId
            VALUES
              (@BacktestRunId, @SymbolId, @Strategy, @Interval, @BarOpenTime, @Side,
               @EntryPrice, @StopLoss, @TakeProfit, @AtrValue, @Regime, @Confidence,
               @Status, @Reason, @CreatedAt);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            BacktestRunId = runId,
            s.SymbolId, s.Strategy, s.Interval, s.BarOpenTime, s.Side,
            s.EntryPrice, s.StopLoss, s.TakeProfit, s.AtrValue, s.Regime,
            s.Confidence, s.Status, s.Reason, s.CreatedAt,
        }, cancellationToken: ct)).ConfigureAwait(false);
        s.SignalId = id;
        return id;
    }
}

internal sealed class BacktestOrderRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestOrderRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(long runId, Order o, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.Orders
              (BacktestRunId, SignalId, SymbolId, AccountType, ClientOrderId, ExchangeOrderId,
               OrderType, Side, PositionSide, Quantity, Price, StopPrice,
               TimeInForce, ReduceOnly, Status, FilledQty, AvgFillPrice,
               CommissionPaid, CommissionAsset, SubmittedAt, LastUpdatedAt, Notes)
            OUTPUT INSERTED.OrderId
            VALUES
              (@BacktestRunId, @SignalId, @SymbolId, @AccountType, @ClientOrderId, @ExchangeOrderId,
               @OrderType, @Side, @PositionSide, @Quantity, @Price, @StopPrice,
               @TimeInForce, @ReduceOnly, @Status, @FilledQty, @AvgFillPrice,
               @CommissionPaid, @CommissionAsset, @SubmittedAt, @LastUpdatedAt, @Notes);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            BacktestRunId = runId,
            o.SignalId, o.SymbolId, o.AccountType, o.ClientOrderId, o.ExchangeOrderId,
            o.OrderType, o.Side, o.PositionSide, o.Quantity, o.Price, o.StopPrice,
            o.TimeInForce, o.ReduceOnly, o.Status, o.FilledQty, o.AvgFillPrice,
            o.CommissionPaid, o.CommissionAsset, o.SubmittedAt, o.LastUpdatedAt, o.Notes,
        }, cancellationToken: ct)).ConfigureAwait(false);
        o.OrderId = id;
        return id;
    }

    public async Task UpdateStatusAsync(
        long orderId, string status, decimal filledQty, decimal? avgFillPrice,
        decimal commissionPaid, string? commissionAsset, DateTime nowUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE bt.Orders
            SET    Status          = @Status,
                   FilledQty       = @FilledQty,
                   AvgFillPrice    = @AvgFillPrice,
                   CommissionPaid  = @CommissionPaid,
                   CommissionAsset = @CommissionAsset,
                   LastUpdatedAt   = @NowUtc
            WHERE  OrderId = @OrderId;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            OrderId         = orderId,
            Status          = status,
            FilledQty       = filledQty,
            AvgFillPrice    = avgFillPrice,
            CommissionPaid  = commissionPaid,
            CommissionAsset = commissionAsset,
            NowUtc          = nowUtc,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

internal sealed class BacktestFillRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestFillRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(long runId, Fill f, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.Fills
              (BacktestRunId, OrderId, TradeId, Quantity, Price, Commission,
               CommissionAsset, IsMaker, TradeTime)
            OUTPUT INSERTED.FillId
            VALUES
              (@BacktestRunId, @OrderId, @TradeId, @Quantity, @Price, @Commission,
               @CommissionAsset, @IsMaker, @TradeTime);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            BacktestRunId = runId,
            f.OrderId, f.TradeId, f.Quantity, f.Price, f.Commission,
            f.CommissionAsset, f.IsMaker, f.TradeTime,
        }, cancellationToken: ct)).ConfigureAwait(false);
        f.FillId = id;
        return id;
    }
}

internal sealed class BacktestPositionRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestPositionRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(long runId, Position p, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.Positions
              (BacktestRunId, SymbolId, AccountType, Side, EntrySignalId, EntryOrderId,
               Quantity, AvgEntryPrice, StopLoss, TakeProfit, InitialRiskUsd,
               OpenedAt, Status)
            OUTPUT INSERTED.PositionId
            VALUES
              (@BacktestRunId, @SymbolId, @AccountType, @Side, @EntrySignalId, @EntryOrderId,
               @Quantity, @AvgEntryPrice, @StopLoss, @TakeProfit, @InitialRiskUsd,
               @OpenedAt, @Status);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            BacktestRunId = runId,
            p.SymbolId, p.AccountType, p.Side, p.EntrySignalId, p.EntryOrderId,
            p.Quantity, p.AvgEntryPrice, p.StopLoss, p.TakeProfit, p.InitialRiskUsd,
            p.OpenedAt, p.Status,
        }, cancellationToken: ct)).ConfigureAwait(false);
        p.PositionId = id;
        return id;
    }

    public async Task CloseAsync(
        long positionId, DateTime closedAtUtc, decimal closePrice, decimal realizedPnlUsd,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE bt.Positions
            SET    Status         = 'CLOSED',
                   ClosedAt       = @ClosedAt,
                   ClosePrice     = @ClosePrice,
                   RealizedPnlUsd = @RealizedPnlUsd
            WHERE  PositionId = @Id;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id              = positionId,
            ClosedAt        = closedAtUtc,
            ClosePrice      = closePrice,
            RealizedPnlUsd  = realizedPnlUsd,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

internal sealed class BacktestTradeHistoryRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestTradeHistoryRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(long runId, BacktestTrade t, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.TradeHistory
              (BacktestRunId, PositionId, SymbolId, Strategy, Regime, Side,
               EntryTime, ExitTime, HoldingMinutes, EntryPrice, ExitPrice, Quantity,
               GrossPnlUsd, FeesUsd, NetPnlUsd, R_Multiple, ExitReason)
            VALUES
              (@BacktestRunId, @PositionId, @SymbolId, @Strategy, @Regime, @Side,
               @EntryTime, @ExitTime, @HoldingMinutes, @EntryPrice, @ExitPrice, @Quantity,
               @GrossPnlUsd, @FeesUsd, @NetPnlUsd, @RMultiple, @ExitReason);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            BacktestRunId = runId,
            t.PositionId, t.SymbolId, t.Strategy, t.Regime, t.Side,
            t.EntryTime, t.ExitTime, t.HoldingMinutes, t.EntryPrice, t.ExitPrice, t.Quantity,
            t.GrossPnlUsd, t.FeesUsd, t.NetPnlUsd, t.RMultiple, t.ExitReason,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BacktestTrade>> GetForRunAsync(long runId, CancellationToken ct)
    {
        const string sql = """
            SELECT TradeHistoryId, PositionId, SymbolId, Strategy, Regime, Side,
                   EntryTime, ExitTime, HoldingMinutes, EntryPrice, ExitPrice, Quantity,
                   GrossPnlUsd, FeesUsd, NetPnlUsd, R_Multiple AS RMultiple, ExitReason
            FROM   bt.TradeHistory
            WHERE  BacktestRunId = @RunId
            ORDER  BY ExitTime ASC, TradeHistoryId ASC;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<BacktestTrade>(
            new CommandDefinition(sql, new { RunId = runId }, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}

internal sealed class BacktestAccountSnapshotRepository
{
    private readonly IDbConnectionFactory _factory;
    public BacktestAccountSnapshotRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(long runId, string accountType, EquityPoint p, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO bt.AccountSnapshots
              (BacktestRunId, AccountType, SnapshotTime, EquityUsd, AvailableUsd,
               UnrealizedPnl, OpenPositions, GrossExposure, NetExposure, Drawdown)
            VALUES
              (@RunId, @AccountType, @SnapshotTime, @EquityUsd, @AvailableUsd,
               @UnrealizedPnl, @OpenPositions, @GrossExposure, @NetExposure, @Drawdown);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId         = runId,
            AccountType   = accountType,
            SnapshotTime  = p.TimeUtc,
            EquityUsd     = p.EquityUsd,
            AvailableUsd  = p.AvailableUsd,
            UnrealizedPnl = p.UnrealizedPnlUsd,
            OpenPositions = p.OpenPositions,
            GrossExposure = p.GrossExposureUsd,
            NetExposure   = p.NetExposureUsd,
            Drawdown      = p.DrawdownPct,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
