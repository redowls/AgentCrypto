using CoreRegime = TradingBot.Core.Indicators.Regime;

namespace TradingBot.AI.Models;

/// Compact regime-classifier input bundle Claude needs (same fields as the
/// §5.4.2 example block). Holding it as a record keeps the prompt-rendering
/// site readable and makes the analyzer trivially unit-testable.
public sealed record RegimeSnapshot(
    string   Symbol,
    string   Interval,
    decimal  Adx14,
    decimal  PlusDi14,
    decimal  MinusDi14,
    decimal  Atr14,
    decimal  Atr50Sma,
    decimal  AtrRatio,
    decimal  BbWidthPct,
    decimal  BbWidthPct50pctl,
    decimal  Ema9,
    decimal  Ema21,
    decimal  Ema50,
    decimal  Ema200,
    decimal  Last20BarSlopePct,
    CoreRegime RuleRegime,
    decimal    RuleConfidence,
    DateTime   AsOfUtc);
