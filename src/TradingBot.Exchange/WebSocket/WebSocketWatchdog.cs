using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.WebSocket;

/// Periodically scans the stream registry. Any stream whose last event is
/// older than <see cref="BinanceOptions.WebSocketStaleAfter"/> raises a single
/// CRITICAL alert via <see cref="IWebSocketAlertSink"/>; we de-duplicate so a
/// long outage does not flood the alert pipeline.
public sealed class WebSocketWatchdog : BackgroundService
{
    private readonly StreamRegistry _registry;
    private readonly IWebSocketAlertSink _alerts;
    private readonly IOptionsMonitor<BinanceOptions> _options;
    private readonly ILogger<WebSocketWatchdog> _log;
    private readonly HashSet<string> _firedAlerts = new(StringComparer.Ordinal);

    public WebSocketWatchdog(
        StreamRegistry registry,
        IWebSocketAlertSink alerts,
        IOptionsMonitor<BinanceOptions> options,
        ILogger<WebSocketWatchdog> log)
    {
        _registry = registry;
        _alerts = alerts;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "WebSocket watchdog tick failed.");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.WebSocketWatchdogInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Tick()
    {
        var staleAfter = _options.CurrentValue.WebSocketStaleAfter;
        var now = DateTime.UtcNow;

        foreach (var rec in _registry.All())
        {
            var health = rec.ToHealth(staleAfter, now);
            if (health.IsStale)
            {
                if (_firedAlerts.Add(health.StreamId))
                {
                    _log.LogCritical(
                        "WS WATCHDOG: stream {StreamId} stale (last event {Last:O}, threshold {Threshold}).",
                        health.StreamId, health.LastEventUtc, staleAfter);
                    _alerts.RaiseStaleStream(health);
                }
            }
            else
            {
                _firedAlerts.Remove(health.StreamId);
            }
        }
    }
}
