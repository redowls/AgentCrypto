using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Abstractions;

namespace TradingBot.AI.DependencyInjection;

public static class AiServiceCollectionExtensions
{
    /// Claude client, prompt cache, cost meter wired in S9.
    public static IServiceCollection AddAi(this IServiceCollection services, ISecretsProvider secrets)
        => services;
}
