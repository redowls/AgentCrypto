using System.ComponentModel.DataAnnotations;

namespace TradingBot.Worker.Configuration;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    [Required, MinLength(8)]
    public string Model { get; init; } = "claude-sonnet-4-5-20250929";

    [Range(1, 1000)]
    public decimal MonthlyBudgetUsd { get; init; } = 60m;

    [Range(1, 600)]
    public int RequestsPerMinute { get; init; } = 10;
}
