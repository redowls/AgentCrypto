namespace TradingBot.AI.Abstractions;

/// <summary>
/// Thin wrapper around the Anthropic Messages API used by every §5 use case.
/// The implementation handles:
///   • Model selection (default from <c>ClaudeOptions.Model</c>, per-call
///     override via <paramref name="modelOverride"/>);
///   • Anthropic prompt-cache headers (when <c>cache.UseAnthropicPromptCache</c>
///     is true the system block is annotated with <c>cache_control</c>);
///   • Local SHA-256 cache lookup against <c>dbo.AiInteractions</c>
///     (when <c>cache.LocalCacheTtl</c> &gt; 0);
///   • Token-bucket rate limiting (10 RPM by default);
///   • Daily cost cap — when exceeded, the call throws
///     <see cref="AiBudgetExceededException"/> so the use case falls back to
///     rule-only behaviour;
///   • Persistence: every call (cache hit or miss) writes a row to
///     <c>dbo.AiInteractions</c>.
///
/// Returns the raw model JSON; callers are responsible for parsing.
/// </summary>
public interface IClaudeClient
{
    Task<AiResponse> SendAsync(
        string            purpose,
        string            systemPrompt,
        string            userPrompt,
        CacheControl      cache,
        string?           jsonSchemaHint = null,
        string?           modelOverride  = null,
        int               maxOutputTokens = 1024,
        CancellationToken cancellationToken = default);
}

/// Thrown when the daily cost cap has been reached. The use cases catch this
/// and degrade gracefully — sentiment skipped, regime stays rule-based,
/// confirmer auto-approves with size_adj=0.7.
public sealed class AiBudgetExceededException : Exception
{
    public AiBudgetExceededException(decimal capUsd, decimal spentUsd)
        : base($"Daily AI cost cap reached: spent ${spentUsd:F4} of ${capUsd:F4}")
    {
        CapUsd   = capUsd;
        SpentUsd = spentUsd;
    }

    public decimal CapUsd   { get; }
    public decimal SpentUsd { get; }
}
