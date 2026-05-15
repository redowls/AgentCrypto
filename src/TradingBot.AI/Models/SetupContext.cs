namespace TradingBot.AI.Models;

/// Inputs to the §5.4.3 setup-confirmation prompt. Mirrors the example
/// "USER" block exactly so the rendered prompt is structurally identical
/// to the design doc.
public sealed record SetupContext(
    string   Strategy,
    string   Symbol,
    string   Side,                  // BUY/SELL
    decimal  Entry,
    decimal  StopLoss,
    decimal  TakeProfit,
    decimal  AtrMultipleStop,       // e.g. 1.5  → "1.5*ATR"
    decimal  AtrMultipleTake,       // e.g. 3.0  → "3*ATR"
    string   RuleRegime,            // e.g. TRENDING_UP
    decimal  RuleAdx,               // e.g. 31
    decimal  SentimentScore6h,      // e.g. +0.42
    int      SentimentItems6h,
    decimal  BreakoutMagnitudePct,  // +0.38 → "+0.38% above prior 20-bar high"
    decimal  VolumeXSma20,          // 1.7 → "1.7x SMA20"
    decimal  Ema200DistancePct,     // +6.4 → "+6.4%"
    string   StrategyHistorySummary,// "3W/2L, avg R = +0.42"
    decimal  RuleConfidence);

/// §5.4.3 verdict shape Claude must return.
public sealed record SetupConfirmation(
    bool     Approve,
    decimal  Confidence,
    IReadOnlyList<string> Concerns,
    decimal  SizeAdj,               // 0.5 .. 1.0
    bool     IsFallback);           // true when timeout/budget triggered fallback
