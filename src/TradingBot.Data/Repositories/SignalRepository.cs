using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class SignalRepository(IDbConnectionFactory connectionFactory) : ISignalRepository
{
    static SignalRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(Signal signal, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Signals
                (SymbolId, Strategy, [Interval], BarOpenTime, Side,
                 EntryPrice, StopLoss, TakeProfit, AtrValue, Regime,
                 SentimentScore, AiConfidence, Confidence, Status, Reason)
            OUTPUT INSERTED.SignalId
            VALUES
                (@SymbolId, @Strategy, @Interval, @BarOpenTime, @Side,
                 @EntryPrice, @StopLoss, @TakeProfit, @AtrValue, @Regime,
                 @SentimentScore, @AiConfidence, @Confidence, @Status, @Reason);
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, signal, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        signal.SignalId = id;
        return id;
    }

    public async Task<Signal?> GetByIdAsync(long signalId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT SignalId, SymbolId, Strategy, [Interval], BarOpenTime, Side,
                   EntryPrice, StopLoss, TakeProfit, AtrValue, Regime,
                   SentimentScore, AiConfidence, Confidence, Status, Reason, CreatedAt
            FROM   dbo.Signals
            WHERE  SignalId = @SignalId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Signal>(
            new CommandDefinition(sql, new { SignalId = signalId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> UpdateStatusAsync(long signalId, string status, string? reason, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Signals
            SET    Status = @Status,
                   Reason = @Reason
            WHERE  SignalId = @SignalId;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { SignalId = signalId, Status = status, Reason = reason },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Signal>> GetByStatusAsync(string status, int top, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(@Top)
                   SignalId, SymbolId, Strategy, [Interval], BarOpenTime, Side,
                   EntryPrice, StopLoss, TakeProfit, AtrValue, Regime,
                   SentimentScore, AiConfidence, Confidence, Status, Reason, CreatedAt
            FROM   dbo.Signals
            WHERE  Status = @Status
            ORDER BY CreatedAt DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Signal>(
            new CommandDefinition(sql, new { Status = status, Top = top },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
