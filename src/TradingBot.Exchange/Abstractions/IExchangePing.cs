namespace TradingBot.Exchange.Abstractions;

public interface IExchangePing
{
    Task<TimeSpan> PingAsync(CancellationToken cancellationToken);
}
