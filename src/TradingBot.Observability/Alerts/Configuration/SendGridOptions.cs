namespace TradingBot.Observability.Alerts.Configuration;

public sealed class SendGridOptions
{
    public const string SectionName = "SendGrid";

    public bool Enabled { get; init; } = false;
    public string ApiKeySecretName { get; init; } = "SendGrid:ApiKey";
    public string From { get; init; } = "bot@example.com";
    public IList<string> To { get; init; } = new List<string>();
    public int RequestTimeoutMs { get; init; } = 10_000;
}
