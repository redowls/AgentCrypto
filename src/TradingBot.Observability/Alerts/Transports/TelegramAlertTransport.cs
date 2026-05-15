using Microsoft.Extensions.Options;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Transports;

public sealed class TelegramAlertTransport(ITelegramSender sender, IOptions<TelegramOptions> opts) : IAlertTransport
{
    public AlertTransportKind Kind => AlertTransportKind.Telegram;

    public Task SendAsync(AlertSeverity severity, string title, string body, CancellationToken cancellationToken)
    {
        var chatId = severity switch
        {
            AlertSeverity.Critical or AlertSeverity.Error => opts.Value.CriticalChatId,
            AlertSeverity.Warn                            => opts.Value.WarnChatId,
            _                                             => opts.Value.InfoChatId,
        };
        if (string.IsNullOrWhiteSpace(chatId)) return Task.CompletedTask;

        var markdown = $"*{Escape(title)}*\n\n{Escape(body)}";
        return sender.SendAsync(chatId, markdown, cancellationToken);
    }

    private static string Escape(string s) =>
        s.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`");
}
