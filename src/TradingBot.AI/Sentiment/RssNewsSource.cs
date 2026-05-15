using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// Fallback RSS / Atom poller — runs whenever
/// <see cref="NewsOptions.RssFeedUrls"/> is non-empty (works in parallel with
/// CryptoPanic; the analyzer dedupes by HeadlineHash).
/// </summary>
internal sealed class RssNewsSource : INewsSource
{
    public const string HttpClientName = "Rss";

    private readonly HttpClient    _http;
    private readonly NewsOptions   _opt;
    private readonly ILogger<RssNewsSource> _log;

    public string Name => "RSS";

    public RssNewsSource(
        IHttpClientFactory      httpFactory,
        IOptions<NewsOptions>   options,
        ILogger<RssNewsSource>  log)
    {
        _http = httpFactory.CreateClient(HttpClientName);
        _opt  = options.Value;
        _log  = log;
    }

    public async Task<IReadOnlyList<NewsItem>> FetchSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        if (_opt.RssFeedUrls.Length == 0) return Array.Empty<NewsItem>();

        var bag = new List<NewsItem>(64);
        foreach (var url in _opt.RssFeedUrls)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                using var ms = new MemoryStream(bytes);
                using var reader = XmlReader.Create(ms, new XmlReaderSettings
                {
                    Async       = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver  = null,
                });
                var feed = SyndicationFeed.Load(reader);
                if (feed is null) continue;

                var sourceLabel = string.IsNullOrEmpty(feed.Title?.Text)
                    ? new Uri(url).Host
                    : feed.Title!.Text;

                foreach (var item in feed.Items)
                {
                    var ts = item.PublishDate.UtcDateTime;
                    if (ts == DateTimeOffset.MinValue.UtcDateTime || ts <= sinceUtc) continue;
                    var title = item.Title?.Text?.Trim();
                    if (string.IsNullOrEmpty(title)) continue;
                    bag.Add(new NewsItem(ts, sourceLabel, title));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "RSS feed {Url} failed", url);
            }
        }

        _log.LogDebug("RSS sources returned {Count} items newer than {Since:o}", bag.Count, sinceUtc);
        return bag;
    }
}
