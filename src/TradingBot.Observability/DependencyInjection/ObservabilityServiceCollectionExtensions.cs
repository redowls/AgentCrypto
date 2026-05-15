using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Serilog.Core;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Observability;
using TradingBot.Exchange.Abstractions;
using TradingBot.Observability.Alerts;
using TradingBot.Observability.Alerts.Configuration;
using TradingBot.Observability.Alerts.Transports;
using TradingBot.Observability.Digest;
using TradingBot.Observability.HealthChecks;
using TradingBot.Observability.Logging;
using TradingBot.Observability.Metrics;
using TradingBot.Observability.WebSocket;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Wires §11 observability: Serilog enrichers, Prometheus metrics, central
    /// <see cref="IAlertSink"/> router with dedup + journal, feature-flagged
    /// Telegram/SendGrid/AppInsights transports, Quartz-driven warn (6h) and
    /// daily (06:00 UTC) digests, health checks (process-alive + kill switches),
    /// and the WS-to-IAlertSink bridge.
    /// </summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ISecretsProvider bootstrapSecrets)
    {
        // ── Logging enrichers ───────────────────────────────────────────
        services.AddOptions<SensitiveLoggingOptions>()
            .Bind(configuration.GetSection(SensitiveLoggingOptions.SectionName));
        services.AddSingleton<ILogEventEnricher, CorrelationIdEnricher>();
        services.AddSingleton<ILogEventEnricher, SensitiveDataEnricher>();

        // ── Metrics ─────────────────────────────────────────────────────
        services.AddSingleton<ITradingMetrics, PrometheusTradingMetrics>();

        // ── Alert routing core ──────────────────────────────────────────
        services.AddOptions<AlertRoutingOptions>()
            .Bind(configuration.GetSection(AlertRoutingOptions.SectionName));
        services.AddSingleton<AlertDedupCache>();
        services.AddSingleton<IAlertSink, AlertRouter>();

        // Always-on transport.
        services.AddSingleton<IAlertTransport, LoggingAlertTransport>();

        // ── Telegram (flagged) ──────────────────────────────────────────
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .Validate(o => !o.Enabled || (!string.IsNullOrEmpty(o.CriticalChatId) && !string.IsNullOrEmpty(o.WarnChatId)),
                "Telegram chat IDs (CriticalChatId, WarnChatId) must be set when Telegram:Enabled=true");

        var telegramEnabled = configuration.GetValue($"{TelegramOptions.SectionName}:Enabled", false);
        if (telegramEnabled)
        {
            var tokenName = configuration[$"{TelegramOptions.SectionName}:BotTokenSecretName"] ?? "Telegram:BotToken";
            var token = bootstrapSecrets.GetOptional(tokenName)
                        ?? throw new InvalidOperationException(
                            $"Telegram:Enabled=true but secret '{tokenName}' is unset");

            services.AddHttpClient("telegram", c => c.BaseAddress = new Uri("https://api.telegram.org/"));
            services.AddSingleton<ITelegramSender>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
                return new TelegramSender(http,
                    sp.GetRequiredService<IOptions<TelegramOptions>>(),
                    token,
                    sp.GetRequiredService<ILogger<TelegramSender>>());
            });
            services.AddSingleton<IAlertTransport, TelegramAlertTransport>();
        }

        // ── SendGrid (flagged) ──────────────────────────────────────────
        services.AddOptions<SendGridOptions>()
            .Bind(configuration.GetSection(SendGridOptions.SectionName));

        var sendGridEnabled = configuration.GetValue($"{SendGridOptions.SectionName}:Enabled", false);
        if (sendGridEnabled)
        {
            var keyName = configuration[$"{SendGridOptions.SectionName}:ApiKeySecretName"] ?? "SendGrid:ApiKey";
            var apiKey = bootstrapSecrets.GetOptional(keyName)
                         ?? throw new InvalidOperationException(
                            $"SendGrid:Enabled=true but secret '{keyName}' is unset");

            services.AddHttpClient("sendgrid", c => c.BaseAddress = new Uri("https://api.sendgrid.com/"));
            services.AddSingleton<IEmailSender>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("sendgrid");
                return new SendGridEmailSender(http,
                    sp.GetRequiredService<IOptions<SendGridOptions>>(),
                    apiKey,
                    sp.GetRequiredService<ILogger<SendGridEmailSender>>());
            });
            services.AddSingleton<IAlertTransport, SendGridAlertTransport>();
        }

        // ── App Insights (flagged) ──────────────────────────────────────
        services.AddOptions<AppInsightsOptions>()
            .Bind(configuration.GetSection(AppInsightsOptions.SectionName));

        var aiEnabled = configuration.GetValue($"{AppInsightsOptions.SectionName}:Enabled", false);
        if (aiEnabled)
        {
            var connName = configuration[$"{AppInsightsOptions.SectionName}:ConnectionStringSecretName"]
                           ?? "AppInsights:ConnectionString";
            var conn = bootstrapSecrets.GetOptional(connName)
                       ?? throw new InvalidOperationException(
                          $"AppInsights:Enabled=true but secret '{connName}' is unset");

            services.AddSingleton(_ =>
            {
                var cfg = TelemetryConfiguration.CreateDefault();
                cfg.ConnectionString = conn;
                return new TelemetryClient(cfg);
            });
            services.AddSingleton<IAlertTransport, AppInsightsAlertTransport>();
        }

        // ── Health checks (resolved via DI by AddHealthChecks().AddCheck<T>()) ──
        services.TryAddSingleton<ProcessAliveHealthCheck>();
        services.TryAddSingleton<KillSwitchHealthCheck>();
        services.TryAddSingleton<BinanceKillSwitchHealthCheck>();

        // ── WS alert bridge: override the default LoggingWebSocketAlertSink
        //    registered by AddBinanceExchange so WS alerts flow through IAlertSink.
        services.AddSingleton<IWebSocketAlertSink, RoutingWebSocketAlertSink>();

        // ── Digest infrastructure ──────────────────────────────────────
        services.AddSingleton<DigestRenderer>();

        // ── Quartz: register the two digest jobs additively. AddQuartz is
        //    idempotent on repeated calls (confirmed via JournalQuartzJob).
        services.AddQuartz(q =>
        {
            // WARN digest, every 6h.
            if (telegramEnabled)
            {
                var key = new JobKey(WarnDigestJob.JobKey);
                q.AddJob<WarnDigestJob>(o => o.WithIdentity(key).StoreDurably());
                q.AddTrigger(t => t.ForJob(key)
                    .WithIdentity(WarnDigestJob.JobKey + "-trigger")
                    .WithCronSchedule("0 0 0/6 ? * *", c => c.InTimeZone(TimeZoneInfo.Utc)));
            }

            // Daily digest at 06:00 UTC.
            if (sendGridEnabled)
            {
                var cron = configuration[$"{AlertRoutingOptions.SectionName}:DailyDigestCronUtc"] ?? "0 0 6 ? * *";
                var key  = new JobKey(DailyDigestJob.JobKey);
                q.AddJob<DailyDigestJob>(o => o.WithIdentity(key).StoreDurably());
                q.AddTrigger(t => t.ForJob(key)
                    .WithIdentity(DailyDigestJob.JobKey + "-trigger")
                    .WithCronSchedule(cron, c => c.InTimeZone(TimeZoneInfo.Utc)));
            }
        });

        return services;
    }
}
