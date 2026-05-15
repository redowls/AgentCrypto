namespace TradingBot.AI.Models;

/// One line of Claude's NDJSON sentiment response (§5.4.1 schema).
public sealed record NdjsonSentiment(
    string  Asset,         // ticker or "GLOBAL"
    decimal Sentiment,     // -1.0 .. +1.0
    decimal Confidence,    //  0.0 .. +1.0
    string  Horizon,       // INTRADAY|SWING|LONG
    string  Rationale,
    bool    Actionable);
