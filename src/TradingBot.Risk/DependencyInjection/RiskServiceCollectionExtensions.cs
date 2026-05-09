using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using TradingBot.Core.Observability;
using TradingBot.Risk.Abstractions;
using TradingBot.Risk.Account;
using TradingBot.Risk.Configuration;
using TradingBot.Risk.Correlation;
using TradingBot.Risk.Funding;
using TradingBot.Risk.KillSwitch;
using TradingBot.Risk.Manager;

namespace TradingBot.Risk.DependencyInjection;

public static class RiskServiceCollectionExtensions
{
    /// <summary>
    /// Wires the §7 risk subsystem:
    ///   • <see cref="IRiskManager"/> (gates a–l per §8.5)
    ///   • <see cref="IAccountSnapshotProvider"/> + 1-minute persistence loop
    ///   • <see cref="ICorrelationService"/> + nightly Quartz refresh job
    ///   • <see cref="IKillSwitch"/> with Redis replication when configured
    ///   • Funding-rate provider — Binance-backed when futures REST client is
    ///     registered, else <see cref="NullFundingRateProvider"/>.
    /// </summary>
    public static IServiceCollection AddRisk(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>();

        services.AddOptions<RiskOptions>()
            .Bind(configuration.GetSection(RiskOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Memory cache used by the funding provider; .NET DI provides the
        // default implementation when AddMemoryCache is registered.
        services.TryAddSingleton<IMemoryCache, MemoryCache>();

        // Risk gate + supporting services.
        services.AddScoped<IRiskManager, Manager.RiskManager>();
        services.AddScoped<IAccountSnapshotProvider, AccountSnapshotProvider>();
        services.AddScoped<IAccountSnapshotPersister, AccountSnapshotPersister>();
        services.AddScoped<IMarkPriceProvider, Pricing.MarkPriceProvider>();
        services.AddScoped<ICorrelationService, CorrelationService>();
        services.AddScoped<ICorrelationRefresher, CorrelationRefresher>();

        // Funding-rate provider — Binance-backed by default; tests / spot-only
        // deployments override with NullFundingRateProvider via TryAddScoped.
        services.AddScoped<IFundingRateProvider, BinanceFundingRateProvider>();

        // Kill switch is a singleton — the global flag MUST be one object
        // across the host process so RiskManager + ExecutionEngine see the
        // same state without coordination.
        services.AddSingleton<IKillSwitch, KillSwitch.KillSwitch>();

        // Hosted services.
        services.AddHostedService<AccountSnapshotHostedService>();

        AddCorrelationRefreshJob(services, configuration);

        return services;
    }

    /// <summary>
    /// Test/spot-only override: use <see cref="NullFundingRateProvider"/> so
    /// the funding-rate veto is a no-op. Also useful in environments without
    /// a UsdFuturesApi REST client wired (BinanceFundingRateProvider would
    /// fail-construct otherwise).
    /// </summary>
    public static IServiceCollection UseNullFundingRateProvider(this IServiceCollection services)
    {
        // Replace any prior registration.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IFundingRateProvider))
                services.RemoveAt(i);
        }
        services.AddScoped<IFundingRateProvider, NullFundingRateProvider>();
        return services;
    }

    private static void AddCorrelationRefreshJob(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(RiskOptions.SectionName);
        var cron = section[nameof(RiskOptions.CorrelationRefreshCron)];
        if (string.IsNullOrWhiteSpace(cron)) cron = "0 0 2 * * ?"; // 02:00 UTC daily.

        // Quartz may already be added by TradingBot.MarketData. AddQuartz is
        // idempotent on the registry; calling it again merges configuration.
        services.AddQuartz(q =>
        {
            var jobKey = new JobKey(CorrelationRefreshJob.JobKey);
            q.AddJob<CorrelationRefreshJob>(opts => opts.WithIdentity(jobKey).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(jobKey)
                .WithIdentity(CorrelationRefreshJob.JobKey + "-trigger")
                .WithCronSchedule(cron, c => c.InTimeZone(TimeZoneInfo.Utc)));
        });
    }
}
