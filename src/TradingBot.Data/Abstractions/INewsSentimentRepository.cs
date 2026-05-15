using TradingBot.Core.Domain;

namespace TradingBot.Data.Abstractions;

public interface INewsSentimentRepository
{
    /// <summary>
    /// Idempotent on the natural key (HeadlineHash, Asset). Returns the new
    /// row id on insert, or the existing row's id when the same headline+asset
    /// has already been classified — the analyzer relies on this to dedupe
    /// the same headline arriving from multiple feeds (n8n + RSS + CryptoPanic).
    /// </summary>
    Task<long> InsertIfNewAsync(NewsSentimentRecord row, CancellationToken cancellationToken);

    /// <summary>
    /// Returns rows for the given asset newer than <paramref name="sinceUtc"/>.
    /// Used by the strategy layer to compute "average sentiment last 6h".
    /// </summary>
    Task<IReadOnlyList<NewsSentimentRecord>> GetRecentByAssetAsync(
        string            asset,
        DateTime          sinceUtc,
        CancellationToken cancellationToken);
}
