using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class AiInteractionRepository(IDbConnectionFactory connectionFactory) : IAiInteractionRepository
{
    static AiInteractionRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertIfNewAsync(AiInteraction interaction, CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @ExistingId BIGINT =
                (SELECT TOP(1) AiInteractionId
                 FROM   dbo.AiInteractions WITH (UPDLOCK, HOLDLOCK)
                 WHERE  InputHash = @InputHash AND Model = @Model AND Purpose = @Purpose
                 ORDER BY CreatedAt DESC);

            IF @ExistingId IS NOT NULL
            BEGIN
                SELECT @ExistingId;
                RETURN;
            END

            INSERT INTO dbo.AiInteractions
                (Purpose, Model, InputHash, InputJson, OutputJson,
                 InputTokens, OutputTokens, LatencyMs, CostUsd)
            OUTPUT INSERTED.AiInteractionId
            VALUES
                (@Purpose, @Model, @InputHash, @InputJson, @OutputJson,
                 @InputTokens, @OutputTokens, @LatencyMs, @CostUsd);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, interaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        interaction.AiInteractionId = id;
        return id;
    }

    public async Task<AiInteraction?> GetByHashAsync(
        string purpose,
        string model,
        string inputHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) AiInteractionId, Purpose, Model, InputHash, InputJson, OutputJson,
                          InputTokens, OutputTokens, LatencyMs, CostUsd, CreatedAt
            FROM   dbo.AiInteractions
            WHERE  InputHash = @InputHash AND Model = @Model AND Purpose = @Purpose
            ORDER BY CreatedAt DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<AiInteraction>(
            new CommandDefinition(sql,
                new { Purpose = purpose, Model = model, InputHash = inputHash },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
