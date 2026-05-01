namespace TradingBot.Exchange.Abstractions;

/// Returned to subscribers; disposing it tears down the stream.
public interface IStreamSubscription : IAsyncDisposable
{
    string StreamId { get; }
}
