namespace TradingBot.AI.Abstractions;

/// <summary>
/// §5.5 — Anthropic Message Batches API path used only by the weekly journal
/// (50% discount, ≤24h SLA). For the journal use case the polling loop is
/// fine; it runs once a week. Tests substitute an in-memory fake.
/// </summary>
public interface IClaudeBatchClient
{
    /// <summary>
    /// Submits a single-request batch and synchronously polls until the
    /// batch completes (or <paramref name="cancellationToken"/> fires).
    /// Returns the same shape as <see cref="IClaudeClient.SendAsync"/>.
    /// </summary>
    Task<AiResponse> SendOneShotBatchAsync(
        string            purpose,
        string            systemPrompt,
        string            userPrompt,
        string?           modelOverride = null,
        int               maxOutputTokens = 4096,
        TimeSpan?         pollInterval  = null,
        TimeSpan?         maxWait       = null,
        CancellationToken cancellationToken = default);
}
