using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingBot.Core.Indicators;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Channels;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Engine;
using TradingBot.Strategies.Indicators;
using TradingBot.Strategies.Selection;
using TradingBot.Strategies.Strategies;

namespace TradingBot.Strategies.DependencyInjection;

public static class StrategiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the §5 indicator engine + rule-based regime classifier and the
    /// §6 strategy module set: three IStrategy implementations, the §3.4
    /// StrategySelector, the SignalEngine BackgroundService and the in-process
    /// channels that connect S4 → S6 → S7/S8.
    ///
    /// Depends on the market-data caching/computation layer (IIndicatorCache,
    /// IIndicatorPreCacheService, IBarCloseChannel) registered by
    /// <c>AddMarketData</c>, and on the repositories registered by <c>AddTradingData</c>.
    /// </summary>
    public static IServiceCollection AddStrategies(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Indicator engine + regime classifier (S5) -----------------------
        services.AddSingleton<IRegimeClassifier, RegimeClassifier>();
        services.AddScoped<IIndicatorEngine, IndicatorEngine>();

        // ---- Strategy options (S6) ------------------------------------------
        services.AddOptions<BreakoutDonchianOptions>()
            .Bind(configuration.GetSection(BreakoutDonchianOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptions<MeanReversionBbVwapOptions>()
            .Bind(configuration.GetSection(MeanReversionBbVwapOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptions<TrendEmaAdxOptions>()
            .Bind(configuration.GetSection(TrendEmaAdxOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptions<SignalEngineOptions>()
            .Bind(configuration.GetSection(SignalEngineOptions.SectionName))
            .ValidateDataAnnotations();

        // ---- Strategies (singletons — pure functions of inputs) -------------
        services.AddSingleton<IStrategy, BreakoutDonchianStrategy>();
        services.AddSingleton<IStrategy, MeanReversionBbVwapStrategy>();
        services.AddSingleton<IStrategy, TrendEmaAdxStrategy>();

        // ---- Selector (singleton) -------------------------------------------
        services.AddSingleton<IStrategySelector, StrategySelector>();

        // ---- Downstream signal channel (singleton) --------------------------
        services.AddSingleton<IGeneratedSignalChannel, BoundedGeneratedSignalChannel>();

        // ---- SignalEngine BackgroundService ---------------------------------
        services.AddHostedService<SignalEngine>();

        return services;
    }

    /// <summary>
    /// Backwards-compatible entry point for callers that don't pass IConfiguration.
    /// Skips the IOptions binding — strategies use their default values.
    /// </summary>
    public static IServiceCollection AddStrategies(this IServiceCollection services)
    {
        services.AddSingleton<IRegimeClassifier, RegimeClassifier>();
        services.AddScoped<IIndicatorEngine, IndicatorEngine>();

        services.AddOptions<BreakoutDonchianOptions>();
        services.AddOptions<MeanReversionBbVwapOptions>();
        services.AddOptions<TrendEmaAdxOptions>();
        services.AddOptions<SignalEngineOptions>();

        services.AddSingleton<IStrategy, BreakoutDonchianStrategy>();
        services.AddSingleton<IStrategy, MeanReversionBbVwapStrategy>();
        services.AddSingleton<IStrategy, TrendEmaAdxStrategy>();
        services.AddSingleton<IStrategySelector, StrategySelector>();
        services.AddSingleton<IGeneratedSignalChannel, BoundedGeneratedSignalChannel>();
        services.AddHostedService<SignalEngine>();
        return services;
    }
}
