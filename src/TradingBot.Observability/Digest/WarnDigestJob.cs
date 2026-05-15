using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Data.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Digest;

/// <summary>
/// Every 6h Quartz job: reads WARN rows from <see cref="IAlertJournalRepository"/>
/// for the prior window and posts a rolled-up Telegram message. No-op when
/// the journal is empty or when <c>Telegram:WarnChatId</c> is unset.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WarnDigestJob(
    IAlertJournalRepository journal,
    ITelegramSender telegram,
    IOptions<AlertRoutingOptions> routing,
    IOptions<TelegramOptions> tg,
    IClock clock,
    ILogger<WarnDigestJob> log) : IJob
{
    public const string JobKey = "warn-digest-job";

    public async Task Execute(IJobExecutionContext context)
    {
        var now = clock.UtcNow;
        var since = now - routing.Value.WarnDigestInterval;

        var rows = await journal.GetWindowAsync((byte)AlertSeverity.Warn, since, now, context.CancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0) return;
        if (string.IsNullOrWhiteSpace(tg.Value.WarnChatId))
        {
            log.LogDebug("WarnDigestJob: WarnChatId empty, skipping telegram send");
            return;
        }

        var body = WarnDigestRenderer.Render(rows, since, now);
        await telegram.SendAsync(tg.Value.WarnChatId, body, context.CancellationToken).ConfigureAwait(false);
    }
}
