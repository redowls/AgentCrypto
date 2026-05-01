using Microsoft.Extensions.DependencyInjection;

namespace TradingBot.Backtest.DependencyInjection;

public static class BacktestServiceCollectionExtensions
{
    /// Replay engine, walk-forward, Monte Carlo wired in S10.
    public static IServiceCollection AddBacktest(this IServiceCollection services)
        => services;
}
