using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Backtest.Cli;
using TradingBot.Backtest.Configuration;
using TradingBot.Backtest.Domain;
using TradingBot.Backtest.Engine;
using TradingBot.Backtest.Exchange;
using TradingBot.Backtest.MonteCarlo;
using TradingBot.Backtest.Repositories;
using TradingBot.Backtest.Time;
using TradingBot.Backtest.WalkForward;
using TradingBot.Core.Abstractions;
using TradingBot.Core.Indicators;
using TradingBot.Data.Connection;
using TradingBot.Data.DependencyInjection;
using TradingBot.Execution.Slippage;
using TradingBot.Execution.State;
using TradingBot.MarketData.Caching;
using TradingBot.MarketData.Configuration;
using TradingBot.Risk.Configuration;
using TradingBot.Strategies.Abstractions;
using TradingBot.Strategies.Configuration;
using TradingBot.Strategies.Engine;
using TradingBot.Strategies.Indicators;
using TradingBot.Strategies.Selection;
using TradingBot.Strategies.Strategies;

namespace TradingBot.Backtest;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var (sub, flags, _) = ArgumentParser.Parse(args);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            return sub switch
            {
                "run"  => await RunCommand(flags, cts.Token).ConfigureAwait(false),
                "wfa"  => await WfaCommand(flags, cts.Token).ConfigureAwait(false),
                "mc"   => await McCommand(flags, cts.Token).ConfigureAwait(false),
                _      => PrintHelpAndExit($"Unknown subcommand '{sub}'."),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            return PrintHelpAndExit(null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Backtest failed: {ex}");
            return 1;
        }
    }

    private static int PrintHelpAndExit(string? error)
    {
        if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine(error);
        Console.Error.WriteLine("""
            Usage:
              bt run --strategy <CODE> --symbol <CODE> --from <UTC> --to <UTC> [--notes "..."]
              bt wfa --strategy <CODE> --symbol <CODE> --from <UTC> --to <UTC>
                     [--is-months 6] [--oos-months 1] [--step-months 1]
              bt mc  --runId <id> [--reshuffles 1000] [--skips 100]

            Strategy codes: BREAKOUT_DON | MR_BB_VWAP | TREND_EMA_ADX
            Symbol codes:   any row in dbo.Symbols with the matching exchange.
            Dates:          ISO-8601 UTC (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).
            Reads connection string from the same sources as the Worker:
              Database__ConnectionString env var, or Backtest__Database__ConnectionString,
              or appsettings.{Environment}.json under "Database:ConnectionString".
            """);
        return 64;
    }

    // -----------------------------------------------------------------------

    private static async Task<int> RunCommand(IReadOnlyDictionary<string, string> flags, CancellationToken ct)
    {
        var opts = ArgumentParser.ParseRun(flags);
        await using var sp = BuildServices();
        using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<BacktestEngine>();
        var runId = await engine.RunAsync(opts, ct).ConfigureAwait(false);
        Console.WriteLine($"Backtest run #{runId} completed. Reports in: backtest-output/run-{runId:D8}/");
        return 0;
    }

    private static async Task<int> WfaCommand(IReadOnlyDictionary<string, string> flags, CancellationToken ct)
    {
        var cfg = new WalkForwardConfig
        {
            StrategyCode      = flags.TryGetValue("strategy", out var s) ? s : throw new ArgumentException("--strategy required"),
            SymbolCode        = flags.TryGetValue("symbol",   out var c) ? c : throw new ArgumentException("--symbol required"),
            FromUtc           = ArgumentParser.ParseUtc(flags["from"]),
            ToUtc             = ArgumentParser.ParseUtc(flags["to"]),
            InSampleMonths    = ArgumentParser.ParseInt(flags, "is-months",    6),
            OutOfSampleMonths = ArgumentParser.ParseInt(flags, "oos-months",   1),
            StepMonths        = ArgumentParser.ParseInt(flags, "step-months",  1),
        };

        await using var sp = BuildServices();
        using var scope = sp.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<WalkForwardRunner>();
        var parentId = await runner.RunAsync(cfg, ct).ConfigureAwait(false);
        Console.WriteLine($"WFA parent run #{parentId} completed. See dbo.WalkForwardFolds for verdict.");
        return 0;
    }

    private static async Task<int> McCommand(IReadOnlyDictionary<string, string> flags, CancellationToken ct)
    {
        var cfg = new MonteCarloConfig
        {
            ParentRunId         = ArgumentParser.ParseLong(flags, "runId"),
            ReshuffleIterations = ArgumentParser.ParseInt(flags, "reshuffles", 1000),
            SkipIterations      = ArgumentParser.ParseInt(flags, "skips",       100),
        };
        await using var sp = BuildServices();
        using var scope = sp.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<MonteCarloRunner>();
        await runner.RunAsync(cfg, ct).ConfigureAwait(false);
        Console.WriteLine($"Monte Carlo on parent #{cfg.ParentRunId} completed. Quantiles in dbo.MonteCarloResults.");
        return 0;
    }

    // -----------------------------------------------------------------------

    private static ServiceProvider BuildServices()
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var builder = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true);

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            // Share the Worker's user-secrets store (same UserSecretsId in both csproj files),
            // so `dotnet user-secrets set "Database:ConnectionString" "..."` configures both.
            builder.AddUserSecrets(typeof(Program).Assembly, optional: true);
        }

        var configuration = builder
            .AddEnvironmentVariables()
            .AddEnvironmentVariables(prefix: "Backtest__")
            .Build();

        var connStr =
            configuration["Database:ConnectionString"]
            ?? configuration["ConnectionStrings:TradingDb"]
            ?? throw new InvalidOperationException(
                "No DB connection string. Set Database__ConnectionString or "
                + "ConnectionStrings__TradingDb (env var or appsettings).");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("Microsoft", LogLevel.Warning));

        // ---- Data layer (registers all live dbo.* repositories + DbConnectionFactory).
        services.AddTradingData(connStr);

        // ---- Options bindings (engine + every option set the live components want).
        services.AddOptions<BacktestEngineOptions>().Bind(configuration.GetSection(BacktestEngineOptions.SectionName));
        services.AddOptions<MarketDataOptions>().Bind(configuration.GetSection(MarketDataOptions.SectionName));
        services.AddOptions<RiskOptions>().Bind(configuration.GetSection(RiskOptions.SectionName));
        services.AddOptions<BreakoutDonchianOptions>().Bind(configuration.GetSection(BreakoutDonchianOptions.SectionName));
        services.AddOptions<MeanReversionBbVwapOptions>().Bind(configuration.GetSection(MeanReversionBbVwapOptions.SectionName));
        services.AddOptions<TrendEmaAdxOptions>().Bind(configuration.GetSection(TrendEmaAdxOptions.SectionName));

        // ---- Pure live components reused verbatim.
        services.AddSingleton<SimulatedClock>();
        services.AddSingleton<IClock>(sp => sp.GetRequiredService<SimulatedClock>());
        services.AddSingleton<OrderStateMachine>();
        services.AddSingleton<ISlippageModel>(_ => new DefaultSlippageModel());
        services.AddSingleton<IIndicatorCache, InMemoryIndicatorCache>();
        services.AddSingleton<IRegimeClassifier, RegimeClassifier>();
        services.AddScoped<IIndicatorEngine, IndicatorEngine>();
        services.AddScoped<MarketContextBuilder>();
        services.AddSingleton<IStrategy, BreakoutDonchianStrategy>();
        services.AddSingleton<IStrategy, MeanReversionBbVwapStrategy>();
        services.AddSingleton<IStrategy, TrendEmaAdxStrategy>();
        services.AddSingleton<IStrategySelector, StrategySelector>();

        // ---- Backtest-specific repositories + components.
        services.AddScoped<BacktestRunRepository>();
        services.AddScoped<BacktestSignalRepository>();
        services.AddScoped<BacktestOrderRepository>();
        services.AddScoped<BacktestFillRepository>();
        services.AddScoped<BacktestPositionRepository>();
        services.AddScoped<BacktestTradeHistoryRepository>();
        services.AddScoped<BacktestAccountSnapshotRepository>();
        services.AddScoped<WalkForwardFoldRepository>();
        services.AddScoped<MonteCarloResultRepository>();
        services.AddScoped<BacktestEngine>();
        services.AddScoped<WalkForwardRunner>();
        services.AddScoped<MonteCarloRunner>();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
