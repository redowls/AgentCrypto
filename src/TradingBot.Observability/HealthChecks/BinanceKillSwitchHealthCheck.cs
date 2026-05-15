using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Observability.HealthChecks;

/// <summary>Readiness gate: returns Unhealthy when the Binance HTTP-418 kill switch is engaged.</summary>
public sealed class BinanceKillSwitchHealthCheck(IBinanceKillSwitch killSwitch) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!killSwitch.IsTripped)
            return Task.FromResult(HealthCheckResult.Healthy("binance kill switch off"));

        var data = new Dictionary<string, object>
        {
            ["reason"]        = killSwitch.Reason ?? "(unknown)",
            ["trippedAt"]     = killSwitch.TrippedAtUtc?.ToString("o") ?? string.Empty,
            ["retryAfterUtc"] = killSwitch.RetryAfterUtc?.ToString("o") ?? string.Empty,
        };
        return Task.FromResult(HealthCheckResult.Unhealthy("binance kill switch tripped", data: data));
    }
}
