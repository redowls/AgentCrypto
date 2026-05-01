namespace TradingBot.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTimeOffset UtcNowOffset { get; }
}
