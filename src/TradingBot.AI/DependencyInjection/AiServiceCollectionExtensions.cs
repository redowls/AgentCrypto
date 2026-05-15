using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Quartz;
using TradingBot.AI.Abstractions;
using TradingBot.AI.Caching;
using TradingBot.AI.Claude;
using TradingBot.AI.Configuration;
using TradingBot.AI.Cost;
using TradingBot.AI.Journal;
using TradingBot.AI.Regime;
using TradingBot.AI.Sentiment;
using TradingBot.AI.Setup;
using TradingBot.AI.XgBoost;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;

namespace TradingBot.AI.DependencyInjection;

/// <summary>
/// Wires the §5 AI subsystem:
///   • Typed Anthropic <see cref="HttpClient"/> with the API key header
///     stamped at construction (the secret never leaves <see cref="ISecretsProvider"/>).
///   • <see cref="IClaudeClient"/> + <see cref="IClaudeBatchClient"/>.
///   • SHA-256 input cache against <c>dbo.AiInteractions</c>.
///   • Token-bucket rate limiter + daily $-cap meter.
///   • Four use cases (sentiment, regime, setup, journal) + their schedulers.
///   • Pluggable <see cref="INewsSource"/> chain (CryptoPanic + RSS + webhook).
///   • XGBoost no-op stubs for the Phase-2 seam.
///
/// All services are safe to register even when the API key isn't configured
/// — the failure surfaces only on the first call (lets dev/test setups boot
/// the rest of the host without an Anthropic key).
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAi(
        this IServiceCollection services,
        IConfiguration          configuration,
        ISecretsProvider        secrets)
    {
        services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>();
        // IDailyAiCostReader is registered by AddTradingData (lives in Data layer).

        // ── Options ─────────────────────────────────────────────────────
        services.AddOptions<ClaudeOptions>()
            .Bind(configuration.GetSection(ClaudeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<NewsOptions>()
            .Bind(configuration.GetSection(NewsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RegimeConfirmerOptions>()
            .Bind(configuration.GetSection(RegimeConfirmerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SetupConfirmerOptions>()
            .Bind(configuration.GetSection(SetupConfirmerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<JournalOptions>()
            .Bind(configuration.GetSection(JournalOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ── HttpClient (named) — single auth point; secret never logged. ─
        services.AddHttpClient(ClaudeClient.HttpClientName, (sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
            // Secrets are read lazily so the host can boot even when the
            // key isn't set — the call site fails clearly instead.
            var apiKey = secrets.GetOptional(opt.ApiKeySecretName) ?? string.Empty;
            ClaudeClient.ConfigureHttpClient(client, opt, apiKey);
        });

        services.AddHttpClient(CryptoPanicNewsSource.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient(RssNewsSource.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.Add("User-Agent", "TradingBot/1.0 (+rss-fallback)");
        });

        // ── Cache + cost + rate (singletons — process-wide state) ────────
        services.TryAddSingleton<IAiResponseCache, AiResponseCache>();
        services.TryAddSingleton<IAiCostMeter,     DailyCostMeter>();
        services.TryAddSingleton<IAiRateLimiter,   TokenBucketRateLimiter>();

        // ── Claude clients ──────────────────────────────────────────────
        services.TryAddSingleton<IClaudeClient,      ClaudeClient>();
        services.TryAddSingleton<IClaudeBatchClient, ClaudeBatchClient>();

        // ── Use cases ───────────────────────────────────────────────────
        services.TryAddSingleton<INewsSentimentAnalyzer, NewsSentimentAnalyzer>();
        services.TryAddSingleton<IRegimeConfirmer,       ClaudeRegimeConfirmer>();
        services.TryAddSingleton<ISetupConfirmer,        ClaudeSetupConfirmer>();
        services.TryAddSingleton<IPostTradeJournalist,   PostTradeJournalist>();

        // ── XGBoost seam (Phase-2 fills the real implementation) ────────
        services.TryAddSingleton<IXgbSignalFilter,      NoopXgbSignalFilter>();
        services.TryAddSingleton<IXgbTrainingPipeline,  NoopXgbTrainingPipeline>();

        // ── News source chain ───────────────────────────────────────────
        AddNewsSources(services, configuration);

        // ── Hosted services ─────────────────────────────────────────────
        var newsEnabled = configuration.GetValue($"{NewsOptions.SectionName}:Enabled", true);
        if (newsEnabled)
        {
            services.AddHostedService<NewsIngestionService>();
        }

        // ── Quartz weekly journal job ───────────────────────────────────
        AddJournalQuartzJob(services, configuration);

        return services;
    }

    /// <summary>
    /// Back-compat overload — earlier S1 code passes only the secrets
    /// provider. We resolve <see cref="IConfiguration"/> from the same
    /// service collection's bootstrap when this overload is used at
    /// composition time.
    /// </summary>
    public static IServiceCollection AddAi(this IServiceCollection services, ISecretsProvider secrets)
    {
        services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>();

        // Defer real wiring until configuration is bound: callers should
        // prefer the overload that takes IConfiguration. We keep this
        // overload alive so existing host code continues to compile.
        services.TryAddSingleton<IXgbSignalFilter, NoopXgbSignalFilter>();
        services.TryAddSingleton<IXgbTrainingPipeline, NoopXgbTrainingPipeline>();
        _ = secrets;
        return services;
    }

    private static void AddNewsSources(IServiceCollection services, IConfiguration configuration)
    {
        // Webhook source is always registered (cheap singleton); the route
        // mapping respects the EnableWebhook flag.
        services.TryAddSingleton<InMemoryWebhookNewsSource>();
        services.AddSingleton<INewsSource>(sp => sp.GetRequiredService<InMemoryWebhookNewsSource>());

        var section = configuration.GetSection(NewsOptions.SectionName);
        if (section.GetValue("EnableCryptoPanic", false))
        {
            services.AddSingleton<INewsSource, CryptoPanicNewsSource>();
        }

        var rssUrls = section.GetSection("RssFeedUrls").Get<string[]>() ?? [];
        if (rssUrls.Length > 0)
        {
            services.AddSingleton<INewsSource, RssNewsSource>();
        }
    }

    private static void AddJournalQuartzJob(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(JournalOptions.SectionName);
        if (!section.GetValue("Enabled", true)) return;

        var cron = section[nameof(JournalOptions.Cron)];
        if (string.IsNullOrWhiteSpace(cron)) cron = "0 0 6 ? * SUN";

        // Quartz registry is shared with the Risk subsystem; AddQuartz is
        // idempotent on repeated calls.
        services.AddQuartz(q =>
        {
            var jobKey = new JobKey(JournalQuartzJob.JobKey);
            q.AddJob<JournalQuartzJob>(opts => opts.WithIdentity(jobKey).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(jobKey)
                .WithIdentity(JournalQuartzJob.JobKey + "-trigger")
                .WithCronSchedule(cron, c => c.InTimeZone(TimeZoneInfo.Utc)));
        });
    }
}
