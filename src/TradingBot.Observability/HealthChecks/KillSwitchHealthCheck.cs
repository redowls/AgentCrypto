using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Risk.Abstractions;

namespace TradingBot.Observability.HealthChecks;

/// <summary>Readiness gate: returns Unhealthy when the global KillSwitch is engaged.</summary>
public sealed class KillSwitchHealthCheck(IKillSwitch killSwitch) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!killSwitch.IsTripped)
            return Task.FromResult(HealthCheckResult.Healthy("kill switch off"));

        var data = new Dictionary<string, object>
        {
            ["source"]    = killSwitch.Source.ToString(),
            ["reason"]    = killSwitch.Reason ?? "(unknown)",
            ["trippedAt"] = killSwitch.TrippedAtUtc?.ToString("o") ?? string.Empty,
        };
        return Task.FromResult(HealthCheckResult.Unhealthy("kill switch tripped", data: data));
    }
}
