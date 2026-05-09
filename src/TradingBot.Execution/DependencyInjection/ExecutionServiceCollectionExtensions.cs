using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingBot.Core.Observability;
using TradingBot.Execution.Brackets;
using TradingBot.Execution.Channels;
using TradingBot.Execution.Configuration;
using TradingBot.Execution.Engine;
using TradingBot.Execution.Reconciliation;
using TradingBot.Execution.Slippage;
using TradingBot.Execution.State;
using TradingBot.Execution.Trailing;

namespace TradingBot.Execution.DependencyInjection;

public static class ExecutionServiceCollectionExtensions
{
    /// Wires the §6 / §8 execution subsystem:
    ///   • Approved-intent channel (S7 → S8 bridge).
    ///   • <see cref="OrderStateMachine"/> singleton (pure logic).
    ///   • Bracket placers (spot OCO + futures emulated) + resolver.
    ///   • Slippage model (default).
    ///   • Hosted services: SignalApprovalHostedService, ExecutionEngine,
    ///     UserDataReactor, TrailingStopManager, ReconciliationService.
    public static IServiceCollection AddExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton<ITradingMetrics, NullTradingMetrics>();

        services.AddOptions<ExecutionOptions>()
            .Bind(configuration.GetSection(ExecutionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IApprovedIntentChannel, BoundedApprovedIntentChannel>();
        services.AddSingleton<OrderStateMachine>();
        services.AddSingleton<ISlippageModel>(_ => new DefaultSlippageModel());

        services.AddScoped<SpotOcoBracketPlacer>();
        services.AddScoped<FuturesEmulatedBracketPlacer>();
        services.AddScoped<IBracketPlacerResolver, BracketPlacerResolver>();

        services.AddHostedService<SignalApprovalHostedService>();
        services.AddHostedService<ExecutionEngine>();
        services.AddHostedService<UserDataReactor>();
        services.AddHostedService<TrailingStopManager>();
        services.AddHostedService<ReconciliationService>();

        return services;
    }

    /// Back-compat shim — older callers used the no-arg overload (S6).
    public static IServiceCollection AddExecution(this IServiceCollection services) =>
        throw new InvalidOperationException(
            "AddExecution() requires IConfiguration after S8. Update Program.cs to call " +
            "services.AddExecution(builder.Configuration).");
}
