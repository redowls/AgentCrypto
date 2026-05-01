using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingBot.Exchange.Binance;

/// Runs the permission check once at startup. If it throws, the host exits
/// before any trading code is reachable.
public sealed class KeyPermissionVerifierHostedService(
    KeyPermissionVerifier verifier,
    IHostApplicationLifetime lifetime,
    ILogger<KeyPermissionVerifierHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Binance API key permission check FAILED. Stopping host.");
            lifetime.StopApplication();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
