using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Exchange.Abstractions;
using TradingBot.Exchange.Configuration;

namespace TradingBot.Exchange.WebSocket;

/// Tracks active user-data listenKeys and PUTs them on the configured cadence
/// (default 30 minutes; Binance expires at 60 minutes without keepalive).
/// Gateways register and unregister listenKeys via this service.
public interface IListenKeyRegistry
{
    void Register(AccountType account, string listenKey);
    void Unregister(AccountType account);
}

public sealed class ListenKeyKeepaliveService
    : BackgroundService, IListenKeyRegistry
{
    private readonly IBinanceGatewayResolver _gateways;
    private readonly IOptionsMonitor<BinanceOptions> _options;
    private readonly ILogger<ListenKeyKeepaliveService> _log;
    private readonly ConcurrentDictionary<AccountType, string> _keys = new();

    public ListenKeyKeepaliveService(
        IBinanceGatewayResolver gateways,
        IOptionsMonitor<BinanceOptions> options,
        ILogger<ListenKeyKeepaliveService> log)
    {
        _gateways = gateways;
        _options = options;
        _log = log;
    }

    public void Register(AccountType account, string listenKey)
    {
        _keys[account] = listenKey;
        _log.LogInformation("Registered listenKey for {Account} (prefix={Prefix}).",
            account, listenKey[..Math.Min(8, listenKey.Length)]);
    }

    public void Unregister(AccountType account) => _keys.TryRemove(account, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CurrentValue.ListenKeyKeepaliveInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }

            foreach (var (account, key) in _keys.ToArray())
            {
                try
                {
                    await _gateways.Get(account).KeepAliveUserDataStreamAsync(key, stoppingToken).ConfigureAwait(false);
                    _log.LogDebug("listenKey keepalive PUT ok for {Account}.", account);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogError(ex, "listenKey keepalive failed for {Account}.", account);
                }
            }
        }
    }
}
