using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts.Configuration;

public sealed class AlertRoutingOptions
{
    public const string SectionName = "Alerts";

    public string   InstanceId          { get; init; } = "bot";
    public TimeSpan DedupWindow         { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan WarnDigestInterval  { get; init; } = TimeSpan.FromHours(6);
    public string   DailyDigestCronUtc  { get; init; } = "0 0 6 ? * *";

    public Dictionary<AlertSeverity, AlertTransportKind[]> Routes { get; init; } = new()
    {
        [AlertSeverity.Critical] = [AlertTransportKind.Log, AlertTransportKind.Telegram, AlertTransportKind.Email, AlertTransportKind.AppInsights],
        [AlertSeverity.Error]    = [AlertTransportKind.Log, AlertTransportKind.Telegram, AlertTransportKind.AppInsights],
        [AlertSeverity.Warn]     = [AlertTransportKind.Log, AlertTransportKind.AppInsights],
        [AlertSeverity.Info]     = [AlertTransportKind.Log],
    };
}
