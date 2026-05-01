using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;

namespace TradingBot.Worker.HostedServices;

/// S1 placeholder: emits "Bot host started" on startup so smoke tests have a
/// deterministic ready signal in logs. Replaced as real subsystems come online
/// in S3+ (each becomes its own BackgroundService).
internal sealed class StartupBannerHostedService(
    IOptions<BotOptions> botOptions,
    IOptions<BinanceOptions> binanceOptions,
    ILogger<StartupBannerHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bot = botOptions.Value;
        var bn = binanceOptions.Value;

        logger.LogInformation(
            "Bot host started. Instance={InstanceId} Env={Environment} Symbols={Symbols} " +
            "RiskPerTrade={RiskPct:P2} MaxLev={MaxLev}x BinanceTestnet={Testnet}",
            bot.InstanceId, bot.Environment, string.Join(",", bot.Symbols),
            bot.RiskPerTradePct, bot.MaxLeverage, bn.UseTestnet);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Bot host stopping.");
        return Task.CompletedTask;
    }
}
