using Microsoft.ApplicationInsights;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class AppInsightsAlertTransport(TelemetryClient telemetry) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.AppInsights;

    public Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken)
    {
        var props = new Dictionary<string, string>
        {
            ["severity"] = severity.ToString(),
            ["title"]    = title,
            ["body"]     = body,
        };
        telemetry.TrackEvent("BotAlert", props);
        if (severity == AlertSeverity.Critical)
            telemetry.TrackException(new ApplicationException($"{title}: {body}"), props);
        return Task.CompletedTask;
    }
}
