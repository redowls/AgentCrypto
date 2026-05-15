using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

public interface IAlertTransport
{
    AlertTransportKind Kind { get; }
    Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken);
}
