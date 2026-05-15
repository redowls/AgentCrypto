using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingBot.Observability.HealthChecks;

/// <summary>Always healthy unless the host is dying. Used as the liveness check.</summary>
public sealed class ProcessAliveHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
        => Task.FromResult(HealthCheckResult.Healthy("process alive"));
}
