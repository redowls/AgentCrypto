using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using TradingBot.AI.DependencyInjection;
using TradingBot.Backtest.DependencyInjection;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Time;
using TradingBot.Data.DependencyInjection;
using TradingBot.Exchange.DependencyInjection;
using TradingBot.Execution.DependencyInjection;
using TradingBot.MarketData.DependencyInjection;
using TradingBot.Risk.DependencyInjection;
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

    builder.Services
        .AddOptions<TelegramOptions>()
        .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName));
        // Telegram options validated only when Telegram:Enabled = true (S11.3).

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
    builder.Services.AddRisk();
    builder.Services.AddExecution();
    builder.Services.AddAi(bootstrapSecrets);
    builder.Services.AddBacktest();

    // ---- Hosted services -------------------------------------------------------
    // Order matters: migrations must run before anything that touches the DB.
    builder.Services.AddHostedService<DatabaseMigrationStartupService>();
    builder.Services.AddHostedService<StartupBannerHostedService>();

    // ---- Health checks ---------------------------------------------------------
    var hcBuilder = builder.Services.AddHealthChecks()
        .AddCheck<BinancePingHealthCheck>("binance", tags: ["live", "ready"])
        .AddCheck<WebSocketHealthCheck>("websocket", tags: ["live"]);

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

    app.MapHealthChecks("/health/ready", new()
    {
        Predicate = reg => reg.Tags.Contains("ready"),
        ResponseWriter = WriteHealthResponse,
    });

    app.MapHealthChecks("/health/live", new()
    {
        Predicate = reg => reg.Tags.Contains("live"),
        ResponseWriter = WriteHealthResponse,
    });

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
