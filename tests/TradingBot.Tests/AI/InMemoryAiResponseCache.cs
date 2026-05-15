using TradingBot.AI.Abstractions;
using TradingBot.Core.Abstractions;

namespace TradingBot.Tests.AI;

/// In-memory IAiResponseCache for tests. Indexes rows by their natural key
/// (purpose, model, hash) and honors the same TTL semantics as the
/// production Dapper-backed cache.
internal sealed class InMemoryAiResponseCache(IClock clock) : IAiResponseCache
{
    private readonly Dictionary<(string purpose, string model, string hash), Row> _store = new();
    private long _nextId = 1;

    public IReadOnlyDictionary<(string, string, string), Row> Store => _store;

    public Task<CachedAiResponse?> TryGetAsync(string purpose, string model, string inputHash,
        TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero) return Task.FromResult<CachedAiResponse?>(null);
        if (!_store.TryGetValue((purpose, model, inputHash), out var row))
            return Task.FromResult<CachedAiResponse?>(null);
        if (clock.UtcNow - row.CreatedAt > ttl)
            return Task.FromResult<CachedAiResponse?>(null);
        if (row.OutputJson is null)
            return Task.FromResult<CachedAiResponse?>(null);

        return Task.FromResult<CachedAiResponse?>(new CachedAiResponse(
            row.Id, row.OutputJson, row.InputTokens, row.OutputTokens, row.LatencyMs, row.CostUsd));
    }

    public Task<long> StoreAsync(string purpose, string model, string inputHash, string inputJson,
        string? outputJson, int? inputTokens, int? outputTokens, int? latencyMs, decimal? costUsd,
        CancellationToken cancellationToken)
    {
        var id = _nextId++;
        _store[(purpose, model, inputHash)] = new Row(
            id, inputJson, outputJson,
            inputTokens ?? 0, outputTokens ?? 0, latencyMs ?? 0, costUsd ?? 0m, clock.UtcNow);
        return Task.FromResult(id);
    }

    internal sealed record Row(
        long Id, string InputJson, string? OutputJson,
        int InputTokens, int OutputTokens, int LatencyMs, decimal CostUsd, DateTime CreatedAt);
}
