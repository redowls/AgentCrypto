using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Models;
using TradingBot.AI.Prompts;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// §5.4.1 use case. The analyzer:
///   • Builds the §5.4.1 USER block from the batch of items.
///   • Sends it to Claude with the cached system prompt + 5-min local TTL.
///   • Parses the NDJSON response into one verdict per (item, asset) pair.
///   • Persists each verdict to <c>dbo.NewsSentiment</c>.
///
/// On <see cref="AiBudgetExceededException"/> the analyzer logs WARN and
/// returns an empty list — callers degrade silently to "no fresh sentiment
/// available", which matches the §5 fall-back contract.
/// </summary>
internal sealed class NewsSentimentAnalyzer : INewsSentimentAnalyzer
{
    private readonly IClaudeClient        _claude;
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock               _clock;
    private readonly ILogger<NewsSentimentAnalyzer> _log;

    public NewsSentimentAnalyzer(
        IClaudeClient        claude,
        IServiceScopeFactory scopes,
        IClock               clock,
        ILogger<NewsSentimentAnalyzer> log)
    {
        _claude = claude;
        _scopes = scopes;
        _clock  = clock;
        _log    = log;
    }

    public async Task<IReadOnlyList<NdjsonSentiment>> AnalyzeAsync(
        IReadOnlyList<NewsItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return Array.Empty<NdjsonSentiment>();

        AiResponse response;
        try
        {
            response = await _claude.SendAsync(
                purpose:        AiPurposes.Sentiment,
                systemPrompt:   SystemPrompts.Sentiment,
                userPrompt:     UserPromptRenderer.SentimentBatch(items),
                cache:          CacheControl.Sentiment,
                jsonSchemaHint: null,
                modelOverride:  null,
                maxOutputTokens: 1024,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AiBudgetExceededException ex)
        {
            _log.LogWarning("Sentiment skipped: AI daily cap reached ({Spent:F4}/{Cap:F4})",
                ex.SpentUsd, ex.CapUsd);
            return Array.Empty<NdjsonSentiment>();
        }

        var verdicts = ParseNdjson(response.Json);
        if (verdicts.Count == 0)
        {
            _log.LogWarning("Sentiment response had no parseable NDJSON lines (length={Len})", response.Json.Length);
            return Array.Empty<NdjsonSentiment>();
        }

        await PersistAsync(items, verdicts, response, cancellationToken).ConfigureAwait(false);
        return verdicts;
    }

    /// <summary>
    /// Permissive NDJSON parser — Claude's NDJSON output sometimes includes
    /// blank lines or markdown fences. We skip lines that don't parse as a
    /// JSON object with the expected keys, rather than failing the whole batch.
    /// </summary>
    internal static IReadOnlyList<NdjsonSentiment> ParseNdjson(string text)
    {
        var bag = new List<NdjsonSentiment>();
        if (string.IsNullOrWhiteSpace(text)) return bag;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            // Strip markdown code fences if Claude added them.
            if (line.StartsWith("```")) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var asset      = root.TryGetProperty("asset",      out var a) ? a.GetString() ?? "GLOBAL" : "GLOBAL";
                var sentiment  = root.TryGetProperty("sentiment",  out var s) ? GetDecimal(s) : 0m;
                var confidence = root.TryGetProperty("confidence", out var c) ? GetDecimal(c) : 0m;
                var horizon    = root.TryGetProperty("horizon",    out var h) ? h.GetString() ?? "INTRADAY" : "INTRADAY";
                var rationale  = root.TryGetProperty("rationale",  out var r) ? r.GetString() ?? string.Empty : string.Empty;
                var actionable = root.TryGetProperty("actionable", out var ac) && ac.ValueKind == JsonValueKind.True;

                sentiment  = Math.Clamp(sentiment,  -1m, 1m);
                confidence = Math.Clamp(confidence,  0m, 1m);
                if (rationale.Length > 250) rationale = rationale[..250];

                bag.Add(new NdjsonSentiment(asset.ToUpperInvariant(),
                    sentiment, confidence, horizon.ToUpperInvariant(), rationale, actionable));
            }
            catch (JsonException)
            {
                // Skip malformed lines — they shouldn't take down the batch.
            }
        }
        return bag;
    }

    private static decimal GetDecimal(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0m,
        };
    }

    /// <summary>
    /// Each item maps to all verdicts whose asset matches some asset
    /// implied by the headline. Without a strict join key (Claude is free to
    /// return GLOBAL or any subset), we conservatively pair every verdict
    /// with every item in the batch using a shared headline hash —
    /// (HeadlineHash, Asset) is the natural-key UNIQUE in the table, so
    /// duplicates collapse cleanly.
    ///
    /// This mirrors the §5.4.1 design: one batch = one Claude call =
    /// one set of verdicts; the per-asset row is the persistence unit.
    /// </summary>
    private async Task PersistAsync(
        IReadOnlyList<NewsItem>       items,
        IReadOnlyList<NdjsonSentiment> verdicts,
        AiResponse                    response,
        CancellationToken             cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<INewsSentimentRepository>();

        // FK back to dbo.AiInteractions is best-effort — the cache layer
        // owns the row id and the public AiResponse contract intentionally
        // doesn't expose it (would couple every caller to persistence
        // internals). The dedup key on (HeadlineHash, Asset) is what guards
        // against duplicate writes; the FK is a navigational nice-to-have.
        long? aiInteractionId = null;
        _ = response;

        // Map verdicts back to items. If verdicts.Count == items.Count we
        // pair by index (the §5.4.1 prompt asks for "one JSON object per
        // item"); otherwise we attach each verdict to the first item whose
        // headline mentions the asset, falling back to the first item.
        var pairings = ZipVerdictsToItems(items, verdicts);

        foreach (var (item, verdict) in pairings)
        {
            var hash = HeadlineHash(item.Source, item.Headline);
            var row = new NewsSentimentRecord
            {
                ItemTimestamp   = item.TimestampUtc,
                Source          = item.Source,
                HeadlineHash    = hash,
                Headline        = Truncate(item.Headline, 500),
                Asset           = verdict.Asset,
                Sentiment       = verdict.Sentiment,
                Confidence      = verdict.Confidence,
                Horizon         = verdict.Horizon,
                Rationale       = string.IsNullOrEmpty(verdict.Rationale) ? null : verdict.Rationale,
                Actionable      = verdict.Actionable,
                AiInteractionId = aiInteractionId,
                CreatedAt       = _clock.UtcNow,
            };

            try
            {
                await repo.InsertIfNewAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Persist NewsSentiment row failed (asset={Asset}, hash={Hash})",
                    verdict.Asset, hash);
            }
        }
    }

    private static IEnumerable<(NewsItem item, NdjsonSentiment verdict)> ZipVerdictsToItems(
        IReadOnlyList<NewsItem> items, IReadOnlyList<NdjsonSentiment> verdicts)
    {
        if (verdicts.Count == items.Count)
        {
            for (var i = 0; i < items.Count; i++) yield return (items[i], verdicts[i]);
            yield break;
        }

        // Case where Claude emitted multiple verdicts per headline (multi-asset
        // mention) or fewer verdicts than items: pair each verdict with the
        // first item whose headline contains the asset symbol; default to
        // items[0] when no match found.
        foreach (var v in verdicts)
        {
            var match = items.FirstOrDefault(i =>
                v.Asset is { Length: > 0 } && i.Headline.Contains(v.Asset, StringComparison.OrdinalIgnoreCase));
            yield return (match ?? items[0], v);
        }
    }

    private static string HeadlineHash(string source, string headline)
    {
        var canonical = $"{source}|{headline.Trim()}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
