using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Prometheus;
using Serilog;
using TradingBot.AI.DependencyInjection;
using TradingBot.AI.Sentiment;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Time;
using TradingBot.Data.DependencyInjection;
using TradingBot.Exchange.DependencyInjection;
using TradingBot.Execution.DependencyInjection;
using TradingBot.MarketData.DependencyInjection;
using TradingBot.Observability.DependencyInjection;
using TradingBot.Observability.HealthChecks;
using TradingBot.Risk.DependencyInjection;
using TradingBot.Risk.KillSwitch;
using TradingBot.Strategies.DependencyInjection;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.HealthChecks;
using TradingBot.Worker.HostedServices;

// Bootstrap logger — captures startup failures even if DI/config fail.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---- Configuration layering -------------------------------------------------
    // appsettings.json -> appsettings.{Env}.json -> User Secrets (Dev) ->
    // env vars -> Azure Key Vault (non-Dev, when KeyVault:Uri is set).
    builder.Configuration.AddSecretsSources(builder.Configuration, builder.Environment.EnvironmentName);

    // ---- Serilog ---------------------------------------------------------------
    builder.Host.UseSerilog((context, services, logCfg) => logCfg
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ---- Strongly-typed options with DataAnnotations validation ----------------
    builder.Services
        .AddOptions<BotOptions>()
        .Bind(builder.Configuration.GetSection(BotOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services
        .AddOptions<BinanceOptions>()
        .Bind(builder.Configuration.GetSection(BinanceOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services
        .AddOptions<AnthropicOptions>()
        .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // TelegramOptions moved to TradingBot.Observability and bound by AddObservability (§11).

    builder.Services
        .AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName));

    builder.Services
        .AddOptions<KeyVaultOptions>()
        .Bind(builder.Configuration.GetSection(KeyVaultOptions.SectionName));

    // ---- Cross-cutting singletons ----------------------------------------------
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddSingleton<ISecretsProvider, ConfigurationSecretsProvider>();

    // ---- Subsystems (placeholders + real where wired in S1) --------------------
    var dbConn = builder.Configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];
    builder.Services.AddTradingData(dbConn);

    // Build a transient ISecretsProvider here for Exchange/AI registration which
    // need credentials at composition time. The same ConfigurationSecretsProvider
    // is reused at runtime through DI for everything else.
    var bootstrapSecrets = new ConfigurationSecretsProvider(builder.Configuration);

    builder.Services.AddBinanceExchange(builder.Configuration, bootstrapSecrets);
    builder.Services.AddMarketData(builder.Configuration);
    builder.Services.AddStrategies(builder.Configuration);
    builder.Services.AddRisk(builder.Configuration);
    builder.Services.AddExecution(builder.Configuration);
    builder.Services.AddAi(builder.Configuration, bootstrapSecrets);
    builder.Services.AddObservability(builder.Configuration, bootstrapSecrets);

    // ---- Hosted services -------------------------------------------------------
    // Order matters: migrations must run before anything that touches the DB.
    builder.Services.AddHostedService<DatabaseMigrationStartupService>();
    builder.Services.AddHostedService<StartupBannerHostedService>();

    // ---- Health checks ---------------------------------------------------------
    // §11: liveness = process alive only (independent of external services).
    // Readiness = DB + Binance REST + WS + KillSwitch.
    var hcBuilder = builder.Services.AddHealthChecks()
        .AddCheck<ProcessAliveHealthCheck>("process",                       tags: ["live"])
        .AddCheck<BinancePingHealthCheck>("binance",                        tags: ["ready"])
        .AddCheck<WebSocketHealthCheck>("websocket",                        tags: ["ready"])
        .AddCheck<KillSwitchHealthCheck>("killswitch",                      tags: ["ready"])
        .AddCheck<BinanceKillSwitchHealthCheck>("binance_killswitch",       tags: ["ready"]);

    if (!string.IsNullOrWhiteSpace(dbConn))
    {
        hcBuilder.AddSqlServer(dbConn, name: "sqlserver", tags: ["ready"]);
    }

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapHealthChecks("/health", new()
    {
        ResponseWriter = WriteHealthResponse,
    });

    app.MapHealthChecks("/health/readiness", new()
    {
        Predicate = reg => reg.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse,
    });

    app.MapHealthChecks("/health/liveness", new()
    {
        Predicate = reg => reg.Tags.Contains("live"),
        ResponseWriter = WriteHealthResponse,
    });

    // §11 Prometheus exposition.
    app.MapMetrics();

    // §S9 — n8n-friendly news webhook. Bot accepts NDJSON-style or single
    // payloads at POST /newsfeed/push (auth via optional shared secret in
    // News:WebhookSharedSecret).
    app.MapNewsfeedPush();

    // §11 env-gated test endpoint — only registered outside Production.
    if (!builder.Environment.IsProduction())
    {
        app.MapPost("/admin/test-alert",
            async (TestAlertRequest req, IAlertSink alerts, CancellationToken ct) =>
            {
                await alerts.SendAsync(req.Severity, req.Title, req.Body ?? "Test alert", ct);
                return Results.Accepted();
            });
    }

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bot host terminated unexpectedly during startup");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    var payload = new
    {
        status = report.Status.ToString(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
            durationMs = e.Value.Duration.TotalMilliseconds,
            data = e.Value.Data,
            exception = e.Value.Exception?.Message,
        }),
    };
    return context.Response.WriteAsync(
        JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
}

// Make the implicit Program class available for WebApplicationFactory in tests.
public partial class Program;

internal sealed record TestAlertRequest(AlertSeverity Severity, string Title, string? Body);
