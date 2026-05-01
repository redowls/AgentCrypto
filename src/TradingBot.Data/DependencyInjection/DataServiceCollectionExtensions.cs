using Microsoft.Extensions.DependencyInjection;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;
using TradingBot.Data.Migrations;
using TradingBot.Data.Repositories;

namespace TradingBot.Data.DependencyInjection;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connection factory, the DbUp migrator, and all repositories.
    /// `connectionString` may be null in S1-style health-only deployments — in that
    /// case the connection factory is omitted; the DI graph still composes, and
    /// any repository call will throw a clear error at first use.
    /// </summary>
    public static IServiceCollection AddTradingData(
        this IServiceCollection services,
        string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IDbConnectionFactory>(_ => new SqlConnectionFactory(connectionString));
        }

        services.AddSingleton<DatabaseMigrator>();

        services.AddScoped<ISymbolRepository,          SymbolRepository>();
        services.AddScoped<ICandleRepository,          CandleRepository>();
        services.AddScoped<ISignalRepository,          SignalRepository>();
        services.AddScoped<IRegimeRepository,          RegimeRepository>();
        services.AddScoped<IOrderRepository,           OrderRepository>();
        services.AddScoped<IFillRepository,            FillRepository>();
        services.AddScoped<IPositionRepository,        PositionRepository>();
        services.AddScoped<ITradeHistoryRepository,    TradeHistoryRepository>();
        services.AddScoped<IAccountSnapshotRepository, AccountSnapshotRepository>();
        services.AddScoped<IRiskEventRepository,       RiskEventRepository>();
        services.AddScoped<IAiInteractionRepository,   AiInteractionRepository>();

        return services;
    }
}
