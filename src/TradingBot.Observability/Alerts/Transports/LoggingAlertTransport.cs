using Microsoft.Extensions.Logging;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class LoggingAlertTransport(ILogger<LoggingAlertTransport> log) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Log;

    public Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken)
    {
        var level = severity switch
        {
            AlertSeverity.Critical => LogLevel.Critical,
            AlertSeverity.Error    => LogLevel.Error,
            AlertSeverity.Warn     => LogLevel.Warning,
            _                      => LogLevel.Information,
        };
        log.Log(level, "ALERT [{Severity}] {Title}: {Body}", severity, title, body);
        return Task.CompletedTask;
    }
}
