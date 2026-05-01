using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Worker.HealthChecks;

internal sealed class BinancePingHealthCheck(IExchangePing ping) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rtt = await ping.PingAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                description: $"Binance reachable in {rtt.TotalMilliseconds:F1} ms",
                data: new Dictionary<string, object> { ["RttMs"] = rtt.TotalMilliseconds });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Binance ping failed", ex);
        }
    }
}
