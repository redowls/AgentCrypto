using System.ComponentModel.DataAnnotations;

namespace TradingBot.Worker.Configuration;

/// Root, validated options object. Bound from the "Bot" section so that all
/// non-secret behaviour (universe, leverage caps, risk percentages) lives in
/// one strongly-typed shape that fails fast at boot.
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    [Required, MinLength(1)]
    public string Environment { get; init; } = "Development";

    [Required, MinLength(1)]
    public string InstanceId { get; init; } = "bot-local-1";

    [MinLength(1)]
    public IReadOnlyList<string> Symbols { get; init; } = [];

    [Range(0.0001, 0.10)]
    public decimal RiskPerTradePct { get; init; } = 0.01m;

    [Range(1, 5)]
    public int MaxLeverage { get; init; } = 3;
}
