using System.ComponentModel.DataAnnotations;

namespace TradingBot.Observability.Alerts.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// Whether the alert publisher should attempt Telegram dispatch.
    public bool Enabled { get; init; } = false;

    /// Resolved via ISecretsProvider at composition time — NOT a config-bindable value.
    public string BotTokenSecretName { get; init; } = "Telegram:BotToken";

    [Required] public string CriticalChatId { get; init; } = string.Empty;
    [Required] public string WarnChatId     { get; init; } = string.Empty;

    /// Currently unused (S11 routes INFO to log only); kept for forward compat.
    public string InfoChatId  { get; init; } = string.Empty;

    public int RequestTimeoutMs { get; init; } = 10_000;
}
