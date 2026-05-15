using System.Security.Cryptography;
using System.Text;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.Alerts;

public static class AlertFingerprint
{
    public static string Compute(AlertSeverity severity, string title, string body)
    {
        var input = $"{(int)severity}|{title}|{body}";
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
