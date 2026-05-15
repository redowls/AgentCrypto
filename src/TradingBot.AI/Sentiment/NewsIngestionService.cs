using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Models;
using TradingBot.Core.Abstractions;

namespace TradingBot.AI.Sentiment;

/// <summary>
/// Drains every registered <see cref="INewsSource"/> on a fixed cadence (5 min
/// by default per §5.4.1), pages the results through
/// <see cref="INewsSentimentAnalyzer"/> in batches of
/// <see cref="NewsOptions.BatchSize"/>, and advances the per-source watermark
/// when the analyzer accepted the items.
///
/// Watermarks live in process memory only — a restart re-pulls "since now -
/// 5 min" from each source. Combined with the (HeadlineHash, Asset)
/// natural-key dedup in <c>dbo.NewsSentiment</c>, restarts cause at worst a
/// few duplicate Claude calls in the first 5-min window (and the local SHA
/// cache catches even those when the inputs are identical).
/// </summary>
internal sealed class NewsIngestionService : BackgroundService
{
    private readonly IEnumerable<INewsSource>     _sources;
    private readonly INewsSentimentAnalyzer       _analyzer;
    private readonly IOptionsMonitor<NewsOptions> _newsOpt;
    private readonly IClock                       _clock;
    private readonly ILogger<NewsIngestionService> _log;

    public NewsIngestionService(
        IEnumerable<INewsSource>     sources,
        INewsSentimentAnalyzer       analyzer,
        IOptionsMonitor<NewsOptions> newsOpt,
        IClock                       clock,
        ILogger<NewsIngestionService> log)
    {
        _sources  = sources;
        _analyzer = analyzer;
        _newsOpt  = newsOpt;
        _clock    = clock;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_newsOpt.CurrentValue.Enabled)
        {
            _log.LogInformation("News ingestion disabled (News.Enabled=false)");
            return;
        }

        // Per-source watermark — tracks "newest item we've seen" so we don't
        // re-classify old headlines on every poll.
        var watermarks = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var s in _sources) watermarks[s.Name] = _clock.UtcNow.AddMinutes(-5);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _newsOpt.CurrentValue;
            try
            {
                await PollOnceAsync(opts, watermarks, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "News ingestion cycle failed; will retry next interval");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(
        NewsOptions opts, Dictionary<string, DateTime> watermarks, CancellationToken ct)
    {
        var pulled = new List<NewsItem>(64);
        foreach (var source in _sources)
        {
            var since = watermarks.TryGetValue(source.Name, out var w)
                ? w
                : _clock.UtcNow.AddMinutes(-5);
            var items = await source.FetchSinceAsync(since, ct).ConfigureAwait(false);
            if (items.Count == 0) continue;

            // Advance the watermark to the newest item observed.
            DateTime newest = since;
            foreach (var i in items) if (i.TimestampUtc > newest) newest = i.TimestampUtc;
            watermarks[source.Name] = newest;

            pulled.AddRange(items);
            _log.LogDebug("News source {Source} returned {Count} new items", source.Name, items.Count);
        }

        if (pulled.Count == 0) return;

        // Page through in BatchSize chunks. Each chunk is one Claude call.
        for (var i = 0; i < pulled.Count; i += opts.BatchSize)
        {
            var slice = pulled.GetRange(i, Math.Min(opts.BatchSize, pulled.Count - i));
            await _analyzer.AnalyzeAsync(slice, ct).ConfigureAwait(false);
        }
    }
}
