using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;
using TradingBot.Core.Abstractions;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// Free-tier CryptoPanic poller. Returns posts newer than the watermark with
/// titles trimmed to fit a Claude headline. The free API caps at one request
/// per second per IP — the §5 ingestion cadence (5 min) stays well under.
/// </summary>
internal sealed class CryptoPanicNewsSource : INewsSource
{
    public const string HttpClientName = "CryptoPanic";

    private readonly HttpClient    _http;
    private readonly string        _endpoint;
    private readonly string        _apiKey;
    private readonly IClock        _clock;
    private readonly ILogger<CryptoPanicNewsSource> _log;

    public string Name => "CryptoPanic";

    public CryptoPanicNewsSource(
        IHttpClientFactory      httpFactory,
        IOptions<NewsOptions>   newsOpt,
        ISecretsProvider        secrets,
        IClock                  clock,
        ILogger<CryptoPanicNewsSource> log)
    {
        _http     = httpFactory.CreateClient(HttpClientName);
        _endpoint = newsOpt.Value.CryptoPanicEndpoint;
        _apiKey   = secrets.GetRequired(newsOpt.Value.CryptoPanicTokenSecret);
        _clock    = clock;
        _log      = log;
    }

    public async Task<IReadOnlyList<NewsItem>> FetchSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var url = $"{_endpoint}?auth_token={Uri.EscapeDataString(_apiKey)}&public=true";

        try
        {
            var resp = await _http.GetFromJsonAsync<CryptoPanicResponse>(url,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false);
            if (resp?.Results is null) return Array.Empty<NewsItem>();

            var items = new List<NewsItem>(resp.Results.Length);
            foreach (var post in resp.Results)
            {
                if (post.PublishedAt is null || post.Title is null) continue;
                var ts = post.PublishedAt.Value.UtcDateTime;
                if (ts <= sinceUtc) continue;

                items.Add(new NewsItem(
                    TimestampUtc: ts,
                    Source:       string.IsNullOrEmpty(post.Source?.Title) ? "CryptoPanic" : post.Source!.Title!,
                    Headline:     post.Title.Trim()));
            }
            _log.LogDebug("CryptoPanic returned {Count} items newer than {Since:o}", items.Count, sinceUtc);
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "CryptoPanic poll failed; returning empty list");
            return Array.Empty<NewsItem>();
        }
    }

    private sealed class CryptoPanicResponse
    {
        [JsonPropertyName("results")] public CryptoPanicPost[]? Results { get; init; }
    }

    private sealed class CryptoPanicPost
    {
        [JsonPropertyName("title")]        public string? Title       { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
        [JsonPropertyName("source")]       public CryptoPanicSource? Source { get; init; }
    }

    private sealed class CryptoPanicSource
    {
        [JsonPropertyName("title")]  public string? Title  { get; init; }
    }
}
