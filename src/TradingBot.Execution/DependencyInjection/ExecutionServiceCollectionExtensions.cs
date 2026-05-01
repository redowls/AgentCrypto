using Microsoft.Extensions.DependencyInjection;

namespace TradingBot.Execution.DependencyInjection;

public static class ExecutionServiceCollectionExtensions
{
    /// Order state machine, Polly resilience, OCO/bracket emulator wired in S8.
    public static IServiceCollection AddExecution(this IServiceCollection services)
        => services;
}
