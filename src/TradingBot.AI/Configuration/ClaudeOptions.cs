using System.ComponentModel.DataAnnotations;

namespace TradingBot.AI.Configuration;

/// §5 Claude/Anthropic configuration. Keys come from the secrets provider,
/// not from this options block — see <see cref="ApiKeySecretName"/>.
public sealed class ClaudeOptions
{
    public const string SectionName = "Claude";

    /// <summary>Secret name that holds the Anthropic API key (read via
    /// <c>ISecretsProvider.GetRequired</c>). Default matches the existing
    /// user-secrets / Key Vault layout used in S1.</summary>
    [Required, MinLength(1)]
    public string ApiKeySecretName { get; init; } = "Anthropic:ApiKey";

    [Required, MinLength(8)]
    public string Model { get; init; } = "claude-sonnet-4-5";

    [Required, MinLength(8)]
    public string ApiBaseUrl { get; init; } = "https://api.anthropic.com";

    [Required, MinLength(8)]
    public string MessagesPath { get; init; } = "/v1/messages";

    [Required, MinLength(8)]
    public string BatchesPath  { get; init; } = "/v1/messages/batches";

    [Required, MinLength(8)]
    public string AnthropicVersion { get; init; } = "2023-06-01";

    /// <summary>Cap per UTC day. When breached, calls throw
    /// <c>AiBudgetExceededException</c>; use cases catch and fall back.</summary>
    [Range(0.001, 10000)]
    public decimal DailyCapUsd { get; init; } = 2.00m;

    /// <summary>Token-bucket capacity for the §5.5 limiter.</summary>
    [Range(1, 600)]
    public int RequestsPerMinute { get; init; } = 10;

    [Range(1, 60_000)]
    public int RequestTimeoutMs { get; init; } = 15_000;

    /// <summary>Sonnet 4.5 base prices: $3 / MTok input, $15 / MTok output,
    /// 10% on cache reads, 1.25× on cache writes. Override for newer models
    /// without a code change.</summary>
    [Range(0, 1000)]
    public decimal InputPricePerMTokUsd  { get; init; } = 3.00m;

    [Range(0, 1000)]
    public decimal OutputPricePerMTokUsd { get; init; } = 15.00m;

    [Range(0, 10)]
    public decimal CacheReadMultiplier   { get; init; } = 0.10m; // 10% of base input

    [Range(0, 10)]
    public decimal CacheWriteMultiplier  { get; init; } = 1.25m; // 125% of base input

    /// <summary>Batches API discount — 50% off both input and output.</summary>
    [Range(0, 1)]
    public decimal BatchDiscount { get; init; } = 0.50m;
}
