using TradingBot.Backtest.Domain;
using TradingBot.Core.Domain;
using TradingBot.Core.Domain.Enums;

namespace TradingBot.Backtest.Engine;

// Mutable in-memory account state the replay loop maintains. All money flows
// (cash deltas on entry/exit, fee debits, realized PnL) go through here so
// the equity at any bar boundary matches what's persisted to bt.AccountSnapshots.
internal sealed class Bookkeeper
{
    public decimal StartingEquityUsd { get; }
    public decimal CashUsd { get; private set; }
    public decimal HighWaterMarkUsd { get; private set; }
    public string  AccountType { get; }

    private readonly Dictionary<long, OpenPosition> _open = new();
    private long _nextClientCorrelationId = 1;

    public Bookkeeper(decimal startingEquity, string accountType)
    {
        StartingEquityUsd = startingEquity;
        CashUsd           = startingEquity;
        HighWaterMarkUsd  = startingEquity;
        AccountType       = accountType;
    }

    public IReadOnlyDictionary<long, OpenPosition> Open => _open;

    public string NewCorrelationId() => $"BT-{_nextClientCorrelationId++:D10}";

    // Open a position when an entry fill lands. Caller has already inserted
    // the bt.Positions row and supplies the persisted PositionId.
    public void OpenPosition(OpenPosition pos)
    {
        _open[pos.PositionId] = pos;
        // Cash semantics for spot: notional debit on buy, credit on sell.
        // Fees are always a debit.
        if (pos.Side == PositionSides.Long)
        {
            CashUsd -= pos.Quantity * pos.AvgEntryPrice;
        }
        else
        {
            CashUsd += pos.Quantity * pos.AvgEntryPrice;
        }
        CashUsd -= pos.EntryFeesUsd;
    }

    // Close a position at exitPrice (with exit fees). Returns the realized PnL.
    public decimal ClosePosition(long positionId, decimal exitPrice, decimal exitFeesUsd, DateTime closedAtUtc, out OpenPosition closed)
    {
        if (!_open.TryGetValue(positionId, out var pos))
            throw new InvalidOperationException($"Unknown open position {positionId}");
        _open.Remove(positionId);

        if (pos.Side == PositionSides.Long)
        {
            CashUsd += pos.Quantity * exitPrice;
        }
        else
        {
            CashUsd -= pos.Quantity * exitPrice;
        }
        CashUsd -= exitFeesUsd;

        var grossPnl = pos.Side == PositionSides.Long
            ? (exitPrice - pos.AvgEntryPrice) * pos.Quantity
            : (pos.AvgEntryPrice - exitPrice) * pos.Quantity;
        var totalFees = pos.EntryFeesUsd + exitFeesUsd;
        var netPnl    = grossPnl - totalFees;

        // Update HWM after realised PnL.
        if (CashUsd + UnrealizedPnl(decimal.Zero) > HighWaterMarkUsd)
            HighWaterMarkUsd = CashUsd + UnrealizedPnl(decimal.Zero);

        pos.ClosedAtUtc = closedAtUtc;
        pos.ExitPrice   = exitPrice;
        pos.ExitFeesUsd = exitFeesUsd;
        pos.NetPnlUsd   = netPnl;
        pos.GrossPnlUsd = grossPnl;
        closed = pos;
        return netPnl;
    }

    public decimal UnrealizedPnl(decimal markPrice)
    {
        decimal sum = 0m;
        foreach (var p in _open.Values)
        {
            sum += p.Side == PositionSides.Long
                ? (markPrice - p.AvgEntryPrice) * p.Quantity
                : (p.AvgEntryPrice - markPrice) * p.Quantity;
        }
        return sum;
    }

    public decimal Equity(decimal markPrice) => CashUsd + UnrealizedPnl(markPrice);

    public EquityPoint Snapshot(DateTime atUtc, decimal markPrice)
    {
        var equity   = Equity(markPrice);
        if (equity > HighWaterMarkUsd) HighWaterMarkUsd = equity;
        var dd       = HighWaterMarkUsd > 0 ? (equity / HighWaterMarkUsd) - 1m : 0m;
        decimal grossExp = 0m, netExp = 0m;
        foreach (var p in _open.Values)
        {
            var notional = Math.Abs(p.Quantity * markPrice);
            grossExp += notional;
            netExp   += p.Side == PositionSides.Long ? notional : -notional;
        }
        return new EquityPoint(
            TimeUtc:        atUtc,
            EquityUsd:      equity,
            AvailableUsd:   CashUsd,
            UnrealizedPnlUsd: UnrealizedPnl(markPrice),
            OpenPositions:  _open.Count,
            GrossExposureUsd: grossExp,
            NetExposureUsd:   netExp,
            DrawdownPct:    dd);
    }
}

// Mutable record — fields populated as the position lifecycle progresses.
internal sealed class OpenPosition
{
    public long PositionId { get; set; }
    public required int SymbolId { get; init; }
    public required string Side { get; init; }            // LONG / SHORT
    public required decimal Quantity { get; init; }
    public required decimal AvgEntryPrice { get; init; }
    public required decimal StopLoss { get; set; }
    public required decimal TakeProfit { get; set; }
    public required decimal InitialRiskUsd { get; init; }
    public required DateTime OpenedAtUtc { get; init; }
    public long? EntrySignalId { get; init; }
    public long? EntryOrderId  { get; init; }
    public string Strategy { get; init; } = "";
    public string? Regime { get; init; }
    public decimal EntryFeesUsd { get; init; }

    // Bracket clientOrderIds — the SL + TP siblings placed alongside the entry.
    public string StopClientOrderId { get; set; } = "";
    public string TpClientOrderId   { get; set; } = "";

    // Set on close.
    public DateTime? ClosedAtUtc { get; set; }
    public decimal? ExitPrice    { get; set; }
    public decimal? ExitFeesUsd  { get; set; }
    public decimal? GrossPnlUsd  { get; set; }
    public decimal? NetPnlUsd    { get; set; }
}
