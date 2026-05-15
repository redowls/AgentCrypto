using TradingBot.Core.Domain.Enums;
using TradingBot.Exchange.Abstractions;
using TradingBot.Risk.KillSwitch;

namespace TradingBot.Observability.WebSocket;

/// <summary>
/// Default <see cref="IWebSocketAlertSink"/> for production: bridges
/// WS-specific alerts into the central <see cref="IAlertSink"/> pipeline
/// so dedup, journal, and fan-out apply.
/// </summary>
public sealed class RoutingWebSocketAlertSink(IAlertSink alerts) : IWebSocketAlertSink
{
    public void RaiseStaleStream(StreamHealth health) =>
        _ = alerts.SendAsync(
            AlertSeverity.Critical,
            $"WebSocket stream stale: {health.StreamId}",
            $"Account={health.Account}; lastEvent={health.LastEventUtc:O}; reconnects={health.ReconnectCount}; lastError={health.LastError}",
            CancellationToken.None);

    public void RaiseListenKeyExpired(AccountType account) =>
        _ = alerts.SendAsync(
            AlertSeverity.Critical,
            $"Binance listenKey expired: {account}",
            $"Trading should pause until rotation completes for {account}.",
            CancellationToken.None);
}
