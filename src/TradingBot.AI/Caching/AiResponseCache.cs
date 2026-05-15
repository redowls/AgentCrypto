using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.AI.Abstractions;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;

namespace TradingBot.AI.Caching;

/// <summary>
/// Thin wrapper around <see cref="IAiInteractionRepository"/> that adds the
/// TTL check on top of "find by (Purpose, Model, InputHash)". The repository
/// itself is scoped (Dapper + SqlConnection); this cache is registered as a
/// singleton because the Claude client is a singleton — we resolve the
/// repository through an <see cref="IServiceScopeFactory"/> per call.
/// </summary>
internal sealed class AiResponseCache(
    IServiceScopeFactory scopes,
    IClock                clock) : IAiResponseCache
{
    public async Task<CachedAiResponse?> TryGetAsync(
        string purpose, string model, string inputHash, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero) return null;

        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiInteractionRepository>();

        var row = await repo.GetByHashAsync(purpose, model, inputHash, cancellationToken)
            .ConfigureAwait(false);

        if (row is null || row.OutputJson is null) return null;
        if (clock.UtcNow - row.CreatedAt > ttl) return null;

        return new CachedAiResponse(
            AiInteractionId: row.AiInteractionId,
            OutputJson:      row.OutputJson,
            InputTokens:     row.InputTokens  ?? 0,
            OutputTokens:    row.OutputTokens ?? 0,
            LatencyMs:       row.LatencyMs    ?? 0,
            CostUsd:         row.CostUsd      ?? 0m);
    }

    public async Task<long> StoreAsync(
        string  purpose,
        string  model,
        string  inputHash,
        string  inputJson,
        string? outputJson,
        int?    inputTokens,
        int?    outputTokens,
        int?    latencyMs,
        decimal? costUsd,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAiInteractionRepository>();

        var row = new AiInteraction
        {
            Purpose      = purpose,
            Model        = model,
            InputHash    = inputHash,
            InputJson    = inputJson,
            OutputJson   = outputJson,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs    = latencyMs,
            CostUsd      = costUsd,
            CreatedAt    = clock.UtcNow,
        };

        return await repo.InsertIfNewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Convenience: serialise the (system, user) pair as a small
    /// JSON object so the row in <c>InputJson</c> is human-inspectable
    /// without writing a custom viewer.</summary>
    public static string SerializeInput(string systemPrompt, string userPrompt) =>
        JsonSerializer.Serialize(new { system = systemPrompt, user = userPrompt });
}
