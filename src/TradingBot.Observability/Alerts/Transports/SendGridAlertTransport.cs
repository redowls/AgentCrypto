using System.Net;
using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class SendGridAlertTransport(IEmailSender sender, IOptions<SendGridOptions> opts) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Email;

    public Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken)
    {
        if (opts.Value.To.Count == 0) return Task.CompletedTask;

        var subject = $"[{severity}] {title}";
        var html    = $"<p><strong>{WebUtility.HtmlEncode(title)}</strong></p>" +
                      $"<p>{WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</p>";
        return sender.SendAsync(subject, html, opts.Value.To, cancellationToken);
    }
}
