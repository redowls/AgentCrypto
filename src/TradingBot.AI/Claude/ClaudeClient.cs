using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Caching;
using TradingBot.AI.Configuration;
using TradingBot.Core.Observability;

namespace TradingBot.AI.Claude;

/// <summary>
/// Production <see cref="IClaudeClient"/>. Order of operations per S9 spec:
///   1. Compute SHA-256 input hash.
///   2. Local cache lookup against <c>dbo.AiInteractions</c> — hit short-circuits.
///   3. Daily cost cap check — fail throws <see cref="AiBudgetExceededException"/>.
///   4. Token-bucket rate limit wait.
///   5. POST <c>/v1/messages</c> with <c>cache_control: ephemeral</c> on the
///      system block when the caller asked for prompt caching.
///   6. Compute USD cost from usage envelope, record on the daily meter,
///      persist the call to <c>dbo.AiInteractions</c>.
/// All log lines redact the API key; the secret never crosses logging
/// boundaries (the HttpClient header is set at construction time only).
/// </summary>
internal sealed class ClaudeClient : IClaudeClient
{
    private readonly HttpClient        _http;
    private readonly ClaudeOptions     _opt;
    private readonly IAiResponseCache  _cache;
    private readonly IAiCostMeter      _meter;
    private readonly IAiRateLimiter    _limiter;
    private readonly ITradingMetrics   _metrics;
    private readonly ILogger<ClaudeClient> _log;

    public const string HttpClientName = "Anthropic.Messages";

    public ClaudeClient(
        IHttpClientFactory      httpFactory,
        IOptions<ClaudeOptions> options,
        IAiResponseCache        cache,
        IAiCostMeter            meter,
        IAiRateLimiter          limiter,
        ITradingMetrics         metrics,
        ILogger<ClaudeClient>   log)
    {
        _http    = httpFactory.CreateClient(HttpClientName);
        _opt     = options.Value;
        _cache   = cache;
        _meter   = meter;
        _limiter = limiter;
        _metrics = metrics;
        _log     = log;
    }

    public async Task<AiResponse> SendAsync(
        string            purpose,
        string            systemPrompt,
        string            userPrompt,
        CacheControl      cache,
        string?           jsonSchemaHint     = null,
        string?           modelOverride      = null,
        int               maxOutputTokens    = 1024,
        CancellationToken cancellationToken  = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        var model = modelOverride ?? _opt.Model;
        var inputHash = HashHelper.Sha256Hex(purpose, model, systemPrompt, userPrompt);

        // 1. Local cache lookup. Returns the persisted output without
        //    touching the API or the cost meter.
        var cached = await _cache.TryGetAsync(purpose, model, inputHash, cache.LocalCacheTtl, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            _metrics.IncAiCall(purpose, "cache_hit");
            _log.LogDebug("AI cache hit purpose={Purpose} model={Model} ttl={Ttl}",
                purpose, model, cache.LocalCacheTtl);
            return new AiResponse(
                Json:                     cached.OutputJson,
                InputTokens:              cached.InputTokens,
                OutputTokens:             cached.OutputTokens,
                CacheReadInputTokens:     0,
                CacheCreationInputTokens: 0,
                LatencyMs:                cached.LatencyMs,
                CostUsd:                  0m,           // already paid; daily meter doesn't double-count
                FromCache:                true);
        }

        // 2. Daily cap.
        if (!_meter.TryReserve(out var remaining))
        {
            _metrics.IncAiCall(purpose, "cap_exceeded");
            _log.LogWarning("AI daily cap reached purpose={Purpose} cap={Cap}", purpose, _meter.DailyCapUsd);
            throw new AiBudgetExceededException(_meter.DailyCapUsd, _meter.SpentTodayUsd);
        }

        // 3. Rate limit (token bucket — blocks until a slot is available).
        await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 4. Build + POST the request.
        var systemBlock = new ClaudeSystemBlock
        {
            Text         = systemPrompt,
            CacheControl = cache.UseAnthropicPromptCache ? new ClaudeCacheControl() : null,
        };

        var req = new ClaudeMessageRequest
        {
            Model      = model,
            MaxTokens  = maxOutputTokens,
            System     = [systemBlock],
            Messages   = [ new ClaudeMessage { Role = "user", Content = userPrompt } ],
        };

        var sw = Stopwatch.StartNew();
        ClaudeMessageResponse? body;
        try
        {
            using var resp = await _http.PostAsJsonAsync(_opt.MessagesPath, req, ClaudeJson.Options, cancellationToken)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var snippet = await ReadSafeErrorAsync(resp, cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Anthropic Messages API failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {snippet}");
            }

            body = await resp.Content.ReadFromJsonAsync<ClaudeMessageResponse>(ClaudeJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _metrics.IncAiCall(purpose, "error");
            _log.LogError(ex, "Anthropic call failed purpose={Purpose} model={Model} latency={Ms}ms",
                purpose, model, sw.ElapsedMilliseconds);

            // Persist the failure so it shows up in dbo.AiInteractions audits.
            await SafeStoreAsync(purpose, model, inputHash, systemPrompt, userPrompt,
                outputJson: null, inputTokens: 0, outputTokens: 0, latencyMs: (int)sw.ElapsedMilliseconds,
                costUsd: 0m, cancellationToken).ConfigureAwait(false);
            throw;
        }
        sw.Stop();

        if (body is null)
            throw new InvalidOperationException("Anthropic returned an empty response body.");

        var outputText = ExtractText(body);
        var usage = body.Usage ?? new ClaudeUsage();

        var costUsd = ClaudePricing.CostUsd(
            _opt,
            freshInputTokens:         usage.InputTokens,
            cacheReadInputTokens:     usage.CacheReadInputTokens     ?? 0,
            cacheCreationInputTokens: usage.CacheCreationInputTokens ?? 0,
            outputTokens:             usage.OutputTokens,
            batched:                  false);

        _meter.Record(costUsd);
        _metrics.IncAiCall(purpose, "ok");
        _metrics.AddAiCost(purpose, (double)costUsd);

        await SafeStoreAsync(purpose, model, inputHash, systemPrompt, userPrompt,
            outputJson:   outputText,
            inputTokens:  usage.InputTokens
                          + (usage.CacheReadInputTokens ?? 0)
                          + (usage.CacheCreationInputTokens ?? 0),
            outputTokens: usage.OutputTokens,
            latencyMs:    (int)sw.ElapsedMilliseconds,
            costUsd:      costUsd,
            cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "AI call ok purpose={Purpose} model={Model} in={In}/cr={Cr}/cw={Cw} out={Out} cost=${Cost:F5} latency={Ms}ms",
            purpose, model, usage.InputTokens,
            usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0,
            usage.OutputTokens, costUsd, sw.ElapsedMilliseconds);

        return new AiResponse(
            Json:                     outputText,
            InputTokens:              usage.InputTokens,
            OutputTokens:             usage.OutputTokens,
            CacheReadInputTokens:     usage.CacheReadInputTokens     ?? 0,
            CacheCreationInputTokens: usage.CacheCreationInputTokens ?? 0,
            LatencyMs:                (int)sw.ElapsedMilliseconds,
            CostUsd:                  costUsd,
            FromCache:                false);
    }

    private async Task SafeStoreAsync(
        string purpose, string model, string inputHash, string systemPrompt, string userPrompt,
        string? outputJson, int? inputTokens, int? outputTokens, int? latencyMs, decimal? costUsd,
        CancellationToken ct)
    {
        try
        {
            await _cache.StoreAsync(
                purpose, model, inputHash,
                inputJson:   AiResponseCache.SerializeInput(systemPrompt, userPrompt),
                outputJson:  outputJson,
                inputTokens: inputTokens,
                outputTokens: outputTokens,
                latencyMs:   latencyMs,
                costUsd:     costUsd,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception persistEx) when (persistEx is not OperationCanceledException)
        {
            // Persistence is best-effort. A DB outage shouldn't block the
            // bot's decision path — we still return the model output.
            _log.LogWarning(persistEx, "Failed to persist AiInteraction (purpose={Purpose})", purpose);
        }
    }

    private static string ExtractText(ClaudeMessageResponse body)
    {
        if (body.Content is null || body.Content.Length == 0) return string.Empty;

        // Claude returns an array of content blocks (text / tool_use / etc).
        // For our prompts we ask for plain text/JSON, so we concatenate any
        // text blocks. If the model returns nothing usable we surface "" and
        // let the parser produce a structured error to the caller.
        var sb = new StringBuilder(256);
        foreach (var block in body.Content)
        {
            if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
            {
                sb.Append(block.Text);
            }
        }
        return sb.ToString();
    }

    private static async Task<string> ReadSafeErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return raw.Length > 512 ? raw[..512] + "…" : raw;
        }
        catch
        {
            return "(error body unreadable)";
        }
    }

    /// <summary>
    /// Configures the named <see cref="HttpClient"/> registered in DI for the
    /// Claude wrapper. Authoritative location for the API key header — kept
    /// here so the secret value never appears in logs or option dumps.
    /// </summary>
    public static void ConfigureHttpClient(HttpClient client, ClaudeOptions opt, string apiKey)
    {
        client.BaseAddress = new Uri(opt.ApiBaseUrl);
        client.Timeout     = TimeSpan.FromMilliseconds(opt.RequestTimeoutMs);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("x-api-key",         apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", opt.AnthropicVersion);
    }
}

internal static class ClaudeJson
{
    // Property names are pinned via [JsonPropertyName] on the DTOs so the
    // wire format stays exactly what Anthropic expects regardless of the
    // default naming policy. We just want null-omission and case-insensitive
    // reads.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
