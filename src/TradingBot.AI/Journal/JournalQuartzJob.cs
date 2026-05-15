using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using TradingBot.AI.Abstractions;
using TradingBot.Core.Abstractions;

namespace TradingBot.AI.Journal;

/// <summary>
/// Quartz wrapper around <see cref="IPostTradeJournalist"/>. Default cron
/// is <c>0 0 6 ? * SUN</c> (Sunday 06:00 UTC) — see
/// <see cref="Configuration.JournalOptions.Cron"/>.
///
/// Why Quartz and not Hangfire? — the rest of the bot already uses Quartz
/// (correlation refresh, gap detection, account snapshots). The §S9 spec
/// names "Hangfire job" but the design doc lists Hangfire/Quartz as
/// interchangeable schedulers; consolidating on Quartz keeps the operational
/// surface narrower.
/// </summary>
[DisallowConcurrentExecution]
public sealed class JournalQuartzJob : IJob
{
    public const string JobKey = "PostTradeJournalJob";

    private readonly IServiceScopeFactory _scopes;
    private readonly IClock               _clock;
    private readonly ILogger<JournalQuartzJob> _log;

    public JournalQuartzJob(IServiceScopeFactory scopes, IClock clock, ILogger<JournalQuartzJob> log)
    {
        _scopes = scopes;
        _clock  = clock;
        _log    = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var weekEndUtc = _clock.UtcNow;

        await using var scope = _scopes.CreateAsyncScope();
        var journalist = scope.ServiceProvider.GetRequiredService<IPostTradeJournalist>();

        try
        {
            var result = await journalist.GenerateWeeklyJournalAsync(weekEndUtc, ct).ConfigureAwait(false);
            _log.LogInformation(
                "Weekly journal job ran year={Year} week={Week} trades={Trades} skipped={Skipped} reason={Reason}",
                result.IsoYear, result.IsoWeek, result.TradesAnalyzed, result.Skipped, result.SkipReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Weekly journal job failed");
            throw; // let Quartz log the failure for misfire tracking.
        }
    }
}
