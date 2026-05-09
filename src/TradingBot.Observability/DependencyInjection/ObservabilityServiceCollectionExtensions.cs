using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Abstractions;

namespace TradingBot.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Wires logging enrichers, metrics, alert routing, digests, and health checks. Filled across §11 Tasks 6–24.</summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ISecretsProvider bootstrapSecrets)
    {
        // Filled in across Tasks 6–24.
        return services;
    }
}
