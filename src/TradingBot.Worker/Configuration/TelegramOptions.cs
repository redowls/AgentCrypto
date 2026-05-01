using System.ComponentModel.DataAnnotations;

namespace TradingBot.Worker.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// Whether the alert publisher should attempt Telegram dispatch at startup.
    public bool Enabled { get; init; } = false;

    [Required]
    public string CriticalChatId { get; init; } = string.Empty;

    [Required]
    public string WarnChatId { get; init; } = string.Empty;

    [Required]
    public string InfoChatId { get; init; } = string.Empty;
}
