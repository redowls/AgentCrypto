namespace TradingBot.Observability.Alerts;

public interface ITelegramSender
{
    Task SendAsync(string chatId, string markdownBody, CancellationToken cancellationToken);
}
