using CoreRegime = TradingBot.Core.Indicators.Regime;

namespace TradingBot.AI.Models;

/// §5.4.2 — Claude's regime verdict. <see cref="Source"/> indicates whether
/// the rule-based output was kept or overridden. The §S9 spec says: "If
/// Claude disagrees with confidence > 0.7, persist BOTH and use Claude's
/// verdict; otherwise keep rule-based."
public sealed record RegimeConfirmation(
    CoreRegime  FinalRegime,
    decimal     FinalConfidence,
    string      Source,            // "RULE" or "CLAUDE_CONFIRMED"
    CoreRegime  RuleRegime,
    decimal     RuleConfidence,
    CoreRegime? ClaudeRegime,
    decimal?    ClaudeConfidence,
    string?     ClaudeReason);
