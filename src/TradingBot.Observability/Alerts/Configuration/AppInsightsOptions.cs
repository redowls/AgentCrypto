namespace TradingBot.Observability.Alerts.Configuration;

public sealed class AppInsightsOptions
{
    public const string SectionName = "AppInsights";

    public bool Enabled { get; init; } = false;
    public string ConnectionStringSecretName { get; init; } = "AppInsights:ConnectionString";
}
