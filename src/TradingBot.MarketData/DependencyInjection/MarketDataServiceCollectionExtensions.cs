using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Channels;
using TradingBot.MarketData.Configuration;
using TradingBot.MarketData.GapDetection;
using TradingBot.MarketData.Indicators;
using TradingBot.MarketData.Ingestion;
using TradingBot.MarketData.Persistence;

namespace TradingBot.MarketData.DependencyInjection;

public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Wires the full §4 market-data pipeline:
    ///   - <see cref="MarketDataIngestor"/> (REST backfill + WS subscribe).
    ///   - <see cref="CandlePersistor"/> (channel consumer + bulk MERGE).
    ///   - <see cref="GapDetectionJob"/> via Quartz on a 5-min cadence.
    ///   - <see cref="IndicatorPreCacheService"/> (Skender pre-cache).
    ///   - Live-candle + indicator caches (Redis if configured, in-memory otherwise).
    /// </summary>
    public static IServiceCollection AddMarketData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MarketDataOptions>()
            .Bind(configuration.GetSection(MarketDataOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IKlineChannel, BoundedKlineChannel>();
        services.AddSingleton<IBarCloseChannel, BoundedBarCloseChannel>();
        services.AddSingleton<SymbolMapCache>();
        services.AddSingleton<IndicatorPreCacheService>();

        // Cache layer: prefer Redis; otherwise fall back to in-memory.
        AddCaches(services, configuration);

        // Hosted services: ingestor + persistor.
        services.AddHostedService<MarketDataIngestor>();
        services.AddHostedService<CandlePersistor>();

        // Quartz wiring for the gap-detection job.
        AddGapDetection(services, configuration);

        return services;
    }

    private static void AddCaches(IServiceCollection services, IConfiguration configuration)
    {
        var redisConn = configuration.GetSection(MarketDataOptions.SectionName)
            .GetValue<string?>(nameof(MarketDataOptions.RedisConnectionString));

        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            // Single multiplexer per process — StackExchange.Redis is thread-safe
            // and pools its own connections. Disposing happens at host shutdown.
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var log = sp.GetRequiredService<ILogger<RedisConnectionFactory>>();
                try
                {
                    var mux = ConnectionMultiplexer.Connect(redisConn);
                    log.LogInformation("Connected to Redis: {Endpoints}", string.Join(",", mux.GetEndPoints().Select(e => e.ToString())));
                    return mux;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to connect to Redis at startup; ingestion will fail when caches are exercised.");
                    throw;
                }
            });

            services.AddSingleton<ILiveCandleCache, RedisLiveCandleCache>();
            services.AddSingleton<IIndicatorCache, RedisIndicatorCache>();
        }
        else
        {
            services.AddSingleton<ILiveCandleCache, InMemoryLiveCandleCache>();
            services.AddSingleton<IIndicatorCache, InMemoryIndicatorCache>();
        }
    }

    private static void AddGapDetection(IServiceCollection services, IConfiguration configuration)
    {
        // Read the schedule once at composition time. We don't need IOptions
        // for this — the trigger is built once when Quartz starts; live
        // reconfiguration of the cadence isn't a §4 requirement.
        var section = configuration.GetSection(MarketDataOptions.SectionName);
        var scanIntervalText = section[nameof(MarketDataOptions.GapScanInterval)];
        var scanInterval = string.IsNullOrWhiteSpace(scanIntervalText)
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.Parse(scanIntervalText, System.Globalization.CultureInfo.InvariantCulture);

        services.AddQuartz(q =>
        {
            var jobKey = new JobKey(GapDetectionJob.JobKey);

            q.AddJob<GapDetectionJob>(opts => opts.WithIdentity(jobKey));

            q.AddTrigger(t => t
                .ForJob(jobKey)
                .WithIdentity(GapDetectionJob.JobKey + "-trigger")
                // First run: 90s after startup, giving the ingestor time to
                // complete its REST backfill and avoid double-backfilling the
                // same window.
                .StartAt(DateBuilder.FutureDate(90, IntervalUnit.Second))
                .WithSimpleSchedule(s => s
                    .WithMisfireHandlingInstructionNextWithRemainingCount()
                    .RepeatForever()
                    .WithInterval(scanInterval)));
        });

        services.AddQuartzHostedService(opts =>
        {
            // Wait for in-flight jobs to finish on shutdown. The job is short
            // (a few REST round-trips) so the bounded wait is fine.
            opts.WaitForJobsToComplete = true;
        });
    }

    // Marker used purely as the ILogger<T> category for the Redis factory closure.
    private sealed class RedisConnectionFactory { }
}
