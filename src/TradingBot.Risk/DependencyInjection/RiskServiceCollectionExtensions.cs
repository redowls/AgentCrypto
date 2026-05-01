using Microsoft.Extensions.DependencyInjection;

namespace TradingBot.Risk.DependencyInjection;

public static class RiskServiceCollectionExtensions
{
    /// Risk gate is wired in S7 (sizing, caps, DD ladder, kill-switch).
    public static IServiceCollection AddRisk(this IServiceCollection services)
        => services;
}
