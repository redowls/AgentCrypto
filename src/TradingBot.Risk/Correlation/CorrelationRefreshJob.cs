using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using TradingBot.Core.Abstractions;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Risk.Correlation;

/// Quartz wrapper around <see cref="ICorrelationRefresher"/>. Default cron
/// from <see cref="Configuration.RiskOptions.CorrelationRefreshCron"/> —
/// 02:00 UTC daily per the §9 ops schedule.
[DisallowConcurrentExecution]
public sealed class CorrelationRefreshJob : IJob
{
    public const string JobKey = "CorrelationRefreshJob";

    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly ILogger<CorrelationRefreshJob> _log;

    public CorrelationRefreshJob(
        IServiceScopeFactory scopes,
        IClock clock,
        ILogger<CorrelationRefreshJob> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        await using var scope = _scopes.CreateAsyncScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICorrelationRefresher>();

        // AsOf is "today 00:00 UTC" — the matrix is daily-bar derived, so
        // anchoring to midnight makes runs deterministic regardless of cron drift.
        var nowUtc = _clock.UtcNow;
        var asOf = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            await refresher.RefreshAsync(asOf, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "CorrelationRefreshJob failed for asOf={AsOf:o}", asOf);
            throw; // let Quartz log the failure for misfire stats.
        }
    }
}
