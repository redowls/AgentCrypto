using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class NewsSentimentRepository(IDbConnectionFactory connectionFactory) : INewsSentimentRepository
{
    static NewsSentimentRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertIfNewAsync(NewsSentimentRecord row, CancellationToken cancellationToken)
    {
        // Natural-key dedup: (HeadlineHash, Asset) is UNIQUE in 008_news.sql.
        // We use MERGE to keep the round-trip count to 1 — INSERT-then-CATCH
        // would race against the constraint under concurrent ingestion.
        const string sql = """
            DECLARE @ids TABLE (NewsSentimentId BIGINT);

            MERGE dbo.NewsSentiment WITH (HOLDLOCK) AS tgt
            USING (SELECT @HeadlineHash AS HeadlineHash, @Asset AS Asset) AS src
                ON tgt.HeadlineHash = src.HeadlineHash AND tgt.Asset = src.Asset
            WHEN NOT MATCHED BY TARGET THEN INSERT
                (ItemTimestamp, Source, HeadlineHash, Headline, Asset,
                 Sentiment, Confidence, Horizon, Rationale, Actionable, AiInteractionId)
                VALUES
                (@ItemTimestamp, @Source, @HeadlineHash, @Headline, @Asset,
                 @Sentiment, @Confidence, @Horizon, @Rationale, @Actionable, @AiInteractionId)
            OUTPUT INSERTED.NewsSentimentId INTO @ids;

            SELECT TOP(1) NewsSentimentId FROM @ids
            UNION ALL
            SELECT NewsSentimentId FROM dbo.NewsSentiment
            WHERE  HeadlineHash = @HeadlineHash AND Asset = @Asset;
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        row.NewsSentimentId = id;
        return id;
    }

    public async Task<IReadOnlyList<NewsSentimentRecord>> GetRecentByAssetAsync(
        string asset, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT NewsSentimentId, ItemTimestamp, Source, HeadlineHash, Headline,
                   Asset, Sentiment, Confidence, Horizon, Rationale, Actionable,
                   AiInteractionId, CreatedAt
            FROM   dbo.NewsSentiment
            WHERE  Asset = @Asset AND ItemTimestamp >= @Since
            ORDER BY ItemTimestamp DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<NewsSentimentRecord>(
            new CommandDefinition(sql, new { Asset = asset, Since = sinceUtc },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
