using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Domain.Enums;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;

namespace TradingBot.Observability.Digest;

/// <summary>
/// 06:00 UTC Quartz job. Aggregates the previous calendar day from the
/// existing data repos (trades, positions, snapshots) plus AlertJournal and
/// the AI cost reader, renders to HTML via <see cref="DigestRenderer"/>, and
/// sends a single email via <see cref="IEmailSender"/>. No-op when
/// <c>SendGrid:To</c> is empty.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DailyDigestJob(
    IAlertJournalRepository alerts,
    ITradeHistoryRepository trades,
    IPositionRepository positions,
    IAccountSnapshotRepository snapshots,
    IDailyAiCostReader aiCost,
    IEmailSender email,
    IOptions<SendGridOptions> sg,
    IClock clock,
    DigestRenderer renderer,
    ILogger<DailyDigestJob> log) : IJob
{
    public const string JobKey = "daily-digest-job";

    private const int MaxTradesToList = 1000;

    public async Task Execute(IJobExecutionContext context)
    {
        if (sg.Value.To.Count == 0)
        {
            log.LogWarning("DailyDigestJob skipped: SendGrid:To is empty");
            return;
        }

        var ct = context.CancellationToken;
        var now = clock.UtcNow;
        var dayEnd   = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var dayStart = dayEnd.AddDays(-1);

        // Use the primary spot account as the equity anchor. Multi-account
        // aggregation can be wired later if/when futures equity is also tracked.
        var account = AccountTypes.Spot;

        var data = new DigestData(
            DayStartUtc:   dayStart,
            DayEndUtc:     dayEnd,
            ClosedTrades:  await trades.GetInRangeAsync(dayStart, dayEnd, MaxTradesToList, ct).ConfigureAwait(false),
            OpenPositions: await positions.GetOpenAsync(ct).ConfigureAwait(false),
            EquityStart:   await snapshots.GetFirstAtOrAfterAsync(account, dayStart, ct).ConfigureAwait(false),
            EquityEnd:     await snapshots.GetFirstAtOrAfterAsync(account, dayEnd,   ct).ConfigureAwait(false),
            AlertRows:     await alerts.GetWindowAsync(null, dayStart, dayEnd, ct).ConfigureAwait(false),
            AiCostUsd:     await aiCost.GetTotalForDayAsync(dayStart, dayEnd, ct).ConfigureAwait(false));

        var html    = renderer.RenderHtml(data);
        var subject = $"TradingBot daily digest — {dayStart:yyyy-MM-dd}";
        await email.SendAsync(subject, html, sg.Value.To, ct).ConfigureAwait(false);
    }
}
