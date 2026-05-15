using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Caching;
using TradingBot.AI.Configuration;

namespace TradingBot.AI.Claude;

/// <summary>
/// Sends one-request batches to <c>POST /v1/messages/batches</c> and polls
/// until completion. Used only by the §5.4.4 weekly journal — the 50% Batches
/// discount makes it worth the longer SLA when the call runs once a week.
///
/// We share the <see cref="ClaudeClient.HttpClientName"/> HttpClient (same
/// auth headers, same base URL); the daily cost meter records the discounted
/// figure so the cap stays accurate.
/// </summary>
internal sealed class ClaudeBatchClient : IClaudeBatchClient
{
    private readonly HttpClient        _http;
    private readonly ClaudeOptions     _opt;
    private readonly IAiResponseCache  _cache;
    private readonly IAiCostMeter      _meter;
    private readonly ILogger<ClaudeBatchClient> _log;

    public ClaudeBatchClient(
        IHttpClientFactory      httpFactory,
        IOptions<ClaudeOptions> options,
        IAiResponseCache        cache,
        IAiCostMeter            meter,
        ILogger<ClaudeBatchClient> log)
    {
        _http  = httpFactory.CreateClient(ClaudeClient.HttpClientName);
        _opt   = options.Value;
        _cache = cache;
        _meter = meter;
        _log   = log;
    }

    public async Task<AiResponse> SendOneShotBatchAsync(
        string            purpose,
        string            systemPrompt,
        string            userPrompt,
        string?           modelOverride     = null,
        int               maxOutputTokens   = 4096,
        TimeSpan?         pollInterval      = null,
        TimeSpan?         maxWait           = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var model = modelOverride ?? _opt.Model;
        var inputHash = HashHelper.Sha256Hex(purpose, model, systemPrompt, userPrompt);

        if (!_meter.TryReserve(out _))
            throw new AiBudgetExceededException(_meter.DailyCapUsd, _meter.SpentTodayUsd);

        var customId = $"oneshot-{Guid.NewGuid():N}";
        var systemBlock = new ClaudeSystemBlock { Text = systemPrompt }; // batch path: no prompt cache
        var inner = new ClaudeMessageRequest
        {
            Model     = model,
            MaxTokens = maxOutputTokens,
            System    = [ systemBlock ],
            Messages  = [ new ClaudeMessage { Role = "user", Content = userPrompt } ],
        };
        var batchReq = new ClaudeBatchCreateRequest
        {
            Requests = [ new ClaudeBatchRequestItem { CustomId = customId, Params = inner } ],
        };

        var sw = Stopwatch.StartNew();

        // Submit the batch.
        ClaudeBatchResponse? created;
        using (var resp = await _http.PostAsJsonAsync(_opt.BatchesPath, batchReq, ClaudeJson.Options, cancellationToken)
                   .ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Anthropic Batches API submit failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — " +
                    (snippet.Length > 512 ? snippet[..512] + "…" : snippet));
            }
            created = await resp.Content.ReadFromJsonAsync<ClaudeBatchResponse>(ClaudeJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }

        if (created is null || string.IsNullOrEmpty(created.Id))
            throw new InvalidOperationException("Anthropic Batches API returned no batch id.");

        var poll = pollInterval ?? TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromMinutes(30));

        // Poll until "ended". Anthropic's docs use processing_status values
        // "in_progress" → "ended". The results_url appears once ended.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);

            using var statusResp = await _http.GetAsync($"{_opt.BatchesPath}/{created.Id}", cancellationToken)
                .ConfigureAwait(false);
            statusResp.EnsureSuccessStatusCode();
            var status = await statusResp.Content.ReadFromJsonAsync<ClaudeBatchResponse>(ClaudeJson.Options, cancellationToken)
                .ConfigureAwait(false);
            if (status is null) continue;

            if (string.Equals(status.ProcessingStatus, "ended", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(status.ResultsUrl))
            {
                created = status;
                break;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Anthropic batch {created.Id} did not complete within {(maxWait ?? TimeSpan.FromMinutes(30))}.");
        }

        // Fetch results — JSONL, one line per request.
        using var resultsResp = await _http.GetAsync(created.ResultsUrl!, cancellationToken).ConfigureAwait(false);
        resultsResp.EnsureSuccessStatusCode();
        var raw = await resultsResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        sw.Stop();

        ClaudeBatchResultLine? mine = null;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ClaudeBatchResultLine>(line, ClaudeJson.Options);
            if (parsed?.CustomId == customId) { mine = parsed; break; }
        }

        if (mine?.Result?.Type != "succeeded" || mine.Result.Message is null)
            throw new InvalidOperationException(
                $"Anthropic batch {created.Id} returned non-success result type='{mine?.Result?.Type}'");

        var msg = mine.Result.Message;
        var usage = msg.Usage ?? new ClaudeUsage();

        var costUsd = ClaudePricing.CostUsd(
            _opt,
            freshInputTokens:         usage.InputTokens,
            cacheReadInputTokens:     usage.CacheReadInputTokens     ?? 0,
            cacheCreationInputTokens: usage.CacheCreationInputTokens ?? 0,
            outputTokens:             usage.OutputTokens,
            batched:                  true);
        _meter.Record(costUsd);

        var output = msg.Content?.Where(b => b.Type == "text").Aggregate(
            new System.Text.StringBuilder(),
            (sb, b) => sb.Append(b.Text),
            sb => sb.ToString()) ?? string.Empty;

        try
        {
            await _cache.StoreAsync(
                purpose, model, inputHash,
                inputJson:    AiResponseCache.SerializeInput(systemPrompt, userPrompt),
                outputJson:   output,
                inputTokens:  usage.InputTokens
                              + (usage.CacheReadInputTokens ?? 0)
                              + (usage.CacheCreationInputTokens ?? 0),
                outputTokens: usage.OutputTokens,
                latencyMs:    (int)sw.ElapsedMilliseconds,
                costUsd:      costUsd,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception persistEx) when (persistEx is not OperationCanceledException)
        {
            _log.LogWarning(persistEx, "Failed to persist batched AiInteraction (purpose={Purpose})", purpose);
        }

        _log.LogInformation(
            "AI batch ok purpose={Purpose} model={Model} in={In} out={Out} cost=${Cost:F5} latency={Ms}ms",
            purpose, model, usage.InputTokens, usage.OutputTokens, costUsd, sw.ElapsedMilliseconds);

        return new AiResponse(
            Json:                     output,
            InputTokens:              usage.InputTokens,
            OutputTokens:             usage.OutputTokens,
            CacheReadInputTokens:     usage.CacheReadInputTokens     ?? 0,
            CacheCreationInputTokens: usage.CacheCreationInputTokens ?? 0,
            LatencyMs:                (int)sw.ElapsedMilliseconds,
            CostUsd:                  costUsd,
            FromCache:                false);
    }
}
