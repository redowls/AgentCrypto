namespace TradingBot.Backtest.Domain;

// One sample of the equity curve, written every replay step.
public sealed record EquityPoint(
    DateTime TimeUtc,
    decimal  EquityUsd,
    decimal  AvailableUsd,
    decimal  UnrealizedPnlUsd,
    int      OpenPositions,
    decimal  GrossExposureUsd,
    decimal  NetExposureUsd,
    decimal  DrawdownPct);

// Mirror of one bt.TradeHistory row — the canonical input to metrics + MC.
public sealed record BacktestTrade(
    long     TradeHistoryId,
    long     PositionId,
    int      SymbolId,
    string   Strategy,
    string?  Regime,
    string   Side,
    DateTime EntryTime,
    DateTime ExitTime,
    int      HoldingMinutes,
    decimal  EntryPrice,
    decimal  ExitPrice,
    decimal  Quantity,
    decimal  GrossPnlUsd,
    decimal  FeesUsd,
    decimal  NetPnlUsd,
    decimal  RMultiple,
    string   ExitReason);
