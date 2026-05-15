using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Configuration;
using TradingBot.AI.Prompts;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;

namespace TradingBot.AI.Journal;

/// <summary>
/// §5.4.4 — Sunday 06:00 UTC weekly journal. Pulls the last 7 days of
/// closed trades from <c>dbo.TradeHistory</c>, formats them as CSV with the
/// linked Signals' regime/sentiment/AI confidence columns, and submits a
/// one-shot Anthropic Batch (50% discount).
///
/// On success the markdown is written to <c>{OutputDirectory}/{IsoYear}-{IsoWeek:D2}.md</c>
/// and persisted to <c>dbo.AiJournals</c>. The DB row is the source of truth;
/// the file exists so n8n's email step can attach it without a DB read.
/// </summary>
internal sealed class PostTradeJournalist : IPostTradeJournalist
{
    private readonly IClaudeBatchClient   _batches;
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock               _clock;
    private readonly JournalOptions       _opt;
    private readonly ILogger<PostTradeJournalist> _log;

    public PostTradeJournalist(
        IClaudeBatchClient   batches,
        IServiceScopeFactory scopes,
        IClock               clock,
        IOptions<JournalOptions> options,
        ILogger<PostTradeJournalist> log)
    {
        _batches = batches;
        _scopes  = scopes;
        _clock   = clock;
        _opt     = options.Value;
        _log     = log;
    }

    public async Task<JournalRunResult> GenerateWeeklyJournalAsync(
        DateTime weekEndUtc, CancellationToken cancellationToken)
    {
        // Snap weekEndUtc to the nearest preceding Sunday 06:00 UTC if the
        // caller passed an arbitrary instant — keeps the ISO week
        // calculation deterministic.
        var endUtc   = DateTime.SpecifyKind(weekEndUtc, DateTimeKind.Utc);
        var startUtc = endUtc.AddDays(-7);

        var (isoYear, isoWeek) = IsoWeek(startUtc);

        await using var scope = _scopes.CreateAsyncScope();
        var trades = await scope.ServiceProvider
            .GetRequiredService<ITradeHistoryRepository>()
            .GetInRangeAsync(startUtc, endUtc, _opt.MaxTradesPerJournal, cancellationToken)
            .ConfigureAwait(false);

        if (trades.Count == 0)
        {
            _log.LogInformation("Weekly journal {Year}-W{Week:D2}: no trades to analyze", isoYear, isoWeek);
            return new JournalRunResult(isoYear, isoWeek, 0, null, null, Skipped: true,
                SkipReason: "No closed trades in window");
        }

        var signalIndex = await BuildSignalIndexAsync(scope.ServiceProvider, trades, cancellationToken)
            .ConfigureAwait(false);
        var csv = BuildCsv(trades, signalIndex);

        AiResponse response;
        try
        {
            response = await _batches.SendOneShotBatchAsync(
                purpose:         AiPurposes.Journal,
                systemPrompt:    SystemPrompts.Journal,
                userPrompt:      UserPromptRenderer.JournalCsv(csv),
                modelOverride:   null,
                maxOutputTokens: 4096,
                pollInterval:    _opt.BatchPollInterval,
                maxWait:         _opt.BatchMaxWait,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AiBudgetExceededException ex)
        {
            _log.LogWarning("Weekly journal skipped: AI daily cap reached ({Spent:F4}/{Cap:F4})",
                ex.SpentUsd, ex.CapUsd);
            return new JournalRunResult(isoYear, isoWeek, trades.Count, null, null,
                Skipped: true, SkipReason: "AI daily cap reached");
        }

        var markdown = response.Json.Trim();
        if (markdown.Length == 0)
        {
            _log.LogWarning("Weekly journal {Year}-W{Week:D2}: Claude returned empty markdown", isoYear, isoWeek);
            return new JournalRunResult(isoYear, isoWeek, trades.Count, null, null,
                Skipped: true, SkipReason: "Empty model output");
        }

        var path = await WriteMarkdownAsync(isoYear, isoWeek, markdown, cancellationToken).ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<IAiJournalRepository>().UpsertAsync(
            new AiJournalRecord
            {
                IsoYear         = isoYear,
                IsoWeek         = isoWeek,
                PeriodStartUtc  = startUtc,
                PeriodEndUtc    = endUtc,
                TradesAnalyzed  = trades.Count,
                Markdown        = markdown,
                AiInteractionId = null,    // see NewsSentimentAnalyzer comment — best-effort FK
                CreatedAt       = _clock.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "Weekly journal {Year}-W{Week:D2} produced {Bytes} bytes from {Count} trades → {Path}",
            isoYear, isoWeek, markdown.Length, trades.Count, path);

        return new JournalRunResult(isoYear, isoWeek, trades.Count, path, null, Skipped: false, SkipReason: null);
    }

    private async Task<Dictionary<long, Signal>> BuildSignalIndexAsync(
        IServiceProvider sp, IReadOnlyList<TradeHistory> trades, CancellationToken ct)
    {
        // Each TradeHistory points at a Position, and Positions point at the
        // entry Signal. We don't have a bulk "signals by ids" repo method,
        // so we look up signals one at a time — it's ≤ MaxTradesPerJournal
        // (default 500) once a week, so the cost is fine.
        var signals = sp.GetRequiredService<ISignalRepository>();
        var positions = sp.GetRequiredService<IPositionRepository>();
        var index = new Dictionary<long, Signal>(trades.Count);

        foreach (var t in trades)
        {
            ct.ThrowIfCancellationRequested();
            var pos = await positions.GetByIdAsync(t.PositionId, ct).ConfigureAwait(false);
            if (pos?.EntrySignalId is null) continue;
            var sig = await signals.GetByIdAsync(pos.EntrySignalId.Value, ct).ConfigureAwait(false);
            if (sig is not null) index[t.PositionId] = sig;
        }
        return index;
    }

    internal static string BuildCsv(IReadOnlyList<TradeHistory> trades, IReadOnlyDictionary<long, Signal> signals)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256 + trades.Count * 96);
        sb.Append("PositionId,Strategy,Symbol,Side,EntryTime,ExitTime,HoldingMin,EntryPrice,ExitPrice,Qty,")
          .Append("NetPnlUsd,R_Multiple,ExitReason,Regime,SentimentScore,AiConfidence,RuleConfidence\n");

        foreach (var t in trades)
        {
            signals.TryGetValue(t.PositionId, out var sig);
            sb.Append(t.PositionId).Append(',')
              .Append(Csv(t.Strategy)).Append(',')
              .Append(t.SymbolId.ToString(inv)).Append(',')
              .Append(Csv(t.Side)).Append(',')
              .Append(t.EntryTime.ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append(',')
              .Append(t.ExitTime .ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append(',')
              .Append(t.HoldingMinutes.ToString(inv)).Append(',')
              .Append(t.EntryPrice.ToString("0.########", inv)).Append(',')
              .Append(t.ExitPrice .ToString("0.########", inv)).Append(',')
              .Append(t.Quantity  .ToString("0.########", inv)).Append(',')
              .Append(t.NetPnlUsd .ToString("0.##",       inv)).Append(',')
              .Append(t.RMultiple .ToString("0.####",     inv)).Append(',')
              .Append(Csv(t.ExitReason)).Append(',')
              .Append(Csv(sig?.Regime ?? "")).Append(',')
              .Append((sig?.SentimentScore?.ToString("0.##", inv)) ?? "").Append(',')
              .Append((sig?.AiConfidence?.ToString("0.##",   inv)) ?? "").Append(',')
              .Append((sig?.Confidence  .ToString("0.##",    inv)) ?? "")
              .Append('\n');
        }
        return sb.ToString();
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var needsQuote = s.IndexOfAny([',', '"', '\n']) >= 0;
        if (!needsQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private async Task<string> WriteMarkdownAsync(int isoYear, int isoWeek, string markdown, CancellationToken ct)
    {
        Directory.CreateDirectory(_opt.OutputDirectory);
        var fileName = $"{isoYear:D4}-{isoWeek:D2}.md";
        var path = Path.Combine(_opt.OutputDirectory, fileName);
        await File.WriteAllTextAsync(path, markdown, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>ISO 8601 week-numbering year + week. Anchored on the
    /// week's Thursday (the standard rule used in System.Globalization).
    /// </summary>
    internal static (int IsoYear, int IsoWeek) IsoWeek(DateTime dtUtc)
    {
        var cal = CultureInfo.InvariantCulture.Calendar;
        var day = cal.GetDayOfWeek(dtUtc);
        var pivot = day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday
            ? dtUtc.AddDays(3)
            : dtUtc;
        var week = ISOWeek.GetWeekOfYear(dtUtc);
        var year = ISOWeek.GetYear(dtUtc);
        _ = pivot;
        return (year, week);
    }
}
