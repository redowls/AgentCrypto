using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Worker.HealthChecks;

internal sealed class WebSocketHealthCheck(IWebSocketHealthProbe probe) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snap = probe.Snapshot();
        var data = new Dictionary<string, object>
        {
            ["Status"] = snap.Status.ToString(),
            ["ActiveStreams"] = snap.ActiveStreams,
            ["LastEventUtc"] = snap.LastEventUtc?.ToString("O") ?? "(never)",
        };

        return Task.FromResult(snap.Status switch
        {
            WebSocketHealthStatus.Connected =>
                HealthCheckResult.Healthy("WebSocket streams connected", data),

            WebSocketHealthStatus.NotStarted =>
                HealthCheckResult.Healthy("WebSocket not started yet (S1 stub)", data),

            WebSocketHealthStatus.Reconnecting =>
                HealthCheckResult.Degraded($"WebSocket reconnecting: {snap.LastError}", data: data),

            _ =>
                HealthCheckResult.Unhealthy($"WebSocket disconnected: {snap.LastError}", data: data),
        });
    }
}
