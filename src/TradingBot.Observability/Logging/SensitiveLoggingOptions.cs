namespace TradingBot.Observability.Logging;

public sealed class SensitiveLoggingOptions
{
    public const string SectionName = "Logging:Sensitive";

    public IList<string> RedactedKeys { get; init; } =
        ["ApiKey", "ApiSecret", "BotToken", "Authorization", "Password", "SasToken"];

    public bool MaskOrderQuantities { get; init; } = false;
}
