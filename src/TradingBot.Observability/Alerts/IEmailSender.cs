namespace TradingBot.Observability.Alerts;

public interface IEmailSender
{
    Task SendAsync(string subject, string htmlBody, IEnumerable<string> to, CancellationToken cancellationToken);
}
