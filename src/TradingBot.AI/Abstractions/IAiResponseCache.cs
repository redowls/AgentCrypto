namespace TradingBot.AI.Abstractions;

/// <summary>
/// Local SHA-256(input)-keyed cache backed by <c>dbo.AiInteractions</c>.
/// Looks up the most recent matching row and returns it iff the row is
/// younger than the supplied TTL.
/// </summary>
public interface IAiResponseCache
{
    /// <summary>Returns the cached output JSON when a fresh hit exists.</summary>
    Task<CachedAiResponse?> TryGetAsync(
        string            purpose,
        string            model,
        string            inputHash,
        TimeSpan          ttl,
        CancellationToken cancellationToken);

    /// <summary>Persists the call's outcome and returns the new row id.</summary>
    Task<long> StoreAsync(
        string            purpose,
        string            model,
        string            inputHash,
        string            inputJson,
        string?           outputJson,
        int?              inputTokens,
        int?              outputTokens,
        int?              latencyMs,
        decimal?          costUsd,
        CancellationToken cancellationToken);
}

public sealed record CachedAiResponse(
    long     AiInteractionId,
    string   OutputJson,
    int      InputTokens,
    int      OutputTokens,
    int      LatencyMs,
    decimal  CostUsd);
