using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Core.Abstractions;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.ReferenceData;

/// Loads exchangeInfo on startup, then refreshes daily at the configured UTC
/// time-of-day (default 00:05 UTC).
public sealed class ReferenceDataRefreshHostedService : BackgroundService
{
    private readonly IReferenceDataService _service;
    private readonly IOptionsMonitor<BinanceOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<ReferenceDataRefreshHostedService> _log;

    public ReferenceDataRefreshHostedService(
        IReferenceDataService service,
        IOptionsMonitor<BinanceOptions> options,
        IClock clock,
        ILogger<ReferenceDataRefreshHostedService> log)
    {
        _service = service;
        _options = options;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _service.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _log.LogError(ex, "Initial reference data refresh failed; will retry at next scheduled tick.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ComputeDelayUntilNextRun(_options.CurrentValue.ReferenceDataDailyRefreshUtc);
            _log.LogInformation("Next reference data refresh in {Delay} (UTC tod={Tod}).",
                delay, _options.CurrentValue.ReferenceDataDailyRefreshUtc);

            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }

            try
            {
                await _service.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "Scheduled reference data refresh failed.");
            }
        }
    }

    private TimeSpan ComputeDelayUntilNextRun(TimeSpan timeOfDayUtc)
    {
        var nowUtc = _clock.UtcNow;
        var todayRun = nowUtc.Date.Add(timeOfDayUtc);
        var next = nowUtc < todayRun ? todayRun : todayRun.AddDays(1);
        return next - nowUtc;
    }
}
