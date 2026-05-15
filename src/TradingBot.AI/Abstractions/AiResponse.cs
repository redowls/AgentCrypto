namespace TradingBot.AI.Abstractions;

/// <summary>
/// Normalised result of one Claude Messages call. <see cref="Json"/> is the raw
/// model output (already extracted from the SDK envelope) — callers are
/// responsible for downstream parsing into purpose-specific shapes.
///
/// <see cref="FromCache"/> is true when the response was served from
/// <c>dbo.AiInteractions</c> rather than the live API; in that case
/// <see cref="InputTokens"/>/<see cref="OutputTokens"/>/<see cref="CostUsd"/>
/// reflect the original call's metrics (cost is reported as 0 to keep the
/// daily meter honest).
/// </summary>
public sealed record AiResponse(
    string  Json,
    int     InputTokens,
    int     OutputTokens,
    int     CacheReadInputTokens,
    int     CacheCreationInputTokens,
    int     LatencyMs,
    decimal CostUsd,
    bool    FromCache);
