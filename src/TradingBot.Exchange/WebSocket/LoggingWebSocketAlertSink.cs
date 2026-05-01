using Microsoft.Extensions.Logging;
using TradingBot.Exchange.Abstractions;

namespace TradingBot.Exchange.WebSocket;

/// Default IWebSocketAlertSink. Routes alerts to logs at CRITICAL level until
/// the section 8 alerting subsystem (PagerDuty / Slack) is wired up; replace
/// in DI when that lands.
public sealed class LoggingWebSocketAlertSink(ILogger<LoggingWebSocketAlertSink> logger) : IWebSocketAlertSink
{
    public void RaiseStaleStream(StreamHealth health) =>
        logger.LogCritical(
            "ALERT: WebSocket stream {StreamId} stale on {Account}; last event {Last:O}; reconnects={Reconnects}; lastError={LastError}",
            health.StreamId, health.Account, health.LastEventUtc, health.ReconnectCount, health.LastError);

    public void RaiseListenKeyExpired(AccountType account) =>
        logger.LogCritical("ALERT: Binance listenKey expired for {Account}; trading should pause until rotation completes.", account);
}
