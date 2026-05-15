using Dapper;
using TradingBot.Core.Domain;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;

namespace TradingBot.Data.Repositories;

public sealed class ExecutionDiagnosticsRepository(IDbConnectionFactory connectionFactory) : IExecutionDiagnosticsRepository
{
    static ExecutionDiagnosticsRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(ExecutionDiagnostic row, CancellationToken cancellationToken)
    {
        // Soft-idempotent: if an exec-diag for the same (OrderId, FillId) already
        // exists, return the existing id. A unique index on (OrderId, FillId) would
        // enforce this hard, but FillId is nullable so we de-dup at the app layer.
        const string sql = """
            DECLARE @ExistingId BIGINT =
                (SELECT TOP(1) DiagnosticId FROM dbo.ExecutionDiagnostics WITH (UPDLOCK, HOLDLOCK)
                 WHERE  OrderId = @OrderId
                   AND  ((FillId IS NULL AND @FillId IS NULL) OR FillId = @FillId));

            IF @ExistingId IS NOT NULL
            BEGIN
                SELECT @ExistingId;
                RETURN;
            END

            INSERT INTO dbo.ExecutionDiagnostics
                (OrderId, FillId, SignalId, SymbolId, Side, OrderType,
                 ExpectedPrice, ActualPrice, Quantity,
                 ExpectedSlippageBps, ObservedSlippageBps,
                 SpreadBps, ParticipationPct, ModelVersion)
            OUTPUT INSERTED.DiagnosticId
            VALUES
                (@OrderId, @FillId, @SignalId, @SymbolId, @Side, @OrderType,
                 @ExpectedPrice, @ActualPrice, @Quantity,
                 @ExpectedSlippageBps, @ObservedSlippageBps,
                 @SpreadBps, @ParticipationPct, @ModelVersion);
        """;

        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);
        row.DiagnosticId = id;
        return id;
    }
}

public sealed class BracketLinkRepository(IDbConnectionFactory connectionFactory) : IBracketLinkRepository
{
    static BracketLinkRepository() => DapperBootstrap.EnsureInitialised();

    public async Task<long> InsertAsync(BracketLink link, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.BracketLinks
                (PositionId, StopOrderId, TakeProfitOrderId,
                 StopClientOrderId, TpClientOrderId,
                 AccountType, SymbolId, Status, ReservedSibling)
            OUTPUT INSERTED.BracketLinkId
            VALUES
                (@PositionId, @StopOrderId, @TakeProfitOrderId,
                 @StopClientOrderId, @TpClientOrderId,
                 @AccountType, @SymbolId, @Status, @ReservedSibling);
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, link, cancellationToken: cancellationToken)).ConfigureAwait(false);
        link.BracketLinkId = id;
        return id;
    }

    public async Task<BracketLink?> GetActiveByPositionAsync(long positionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) BracketLinkId, PositionId, StopOrderId, TakeProfitOrderId,
                   StopClientOrderId, TpClientOrderId, AccountType, SymbolId, Status,
                   ReservedSibling, CreatedAt, ResolvedAt
            FROM   dbo.BracketLinks
            WHERE  PositionId = @PositionId AND Status = 'ACTIVE'
            ORDER  BY CreatedAt DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<BracketLink>(
            new CommandDefinition(sql, new { PositionId = positionId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<BracketLink?> GetByLegClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP(1) BracketLinkId, PositionId, StopOrderId, TakeProfitOrderId,
                   StopClientOrderId, TpClientOrderId, AccountType, SymbolId, Status,
                   ReservedSibling, CreatedAt, ResolvedAt
            FROM   dbo.BracketLinks
            WHERE  StopClientOrderId = @Cid OR TpClientOrderId = @Cid
            ORDER  BY CreatedAt DESC;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<BracketLink>(
            new CommandDefinition(sql, new { Cid = clientOrderId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> TryReserveSiblingCancelAsync(long bracketLinkId, string leg, CancellationToken cancellationToken)
    {
        // Atomic CAS on ReservedSibling: succeeds only if the row is ACTIVE
        // and not already reserved.
        const string sql = """
            UPDATE dbo.BracketLinks
            SET    ReservedSibling = @Leg
            WHERE  BracketLinkId = @Id
              AND  Status = 'ACTIVE'
              AND  ReservedSibling IS NULL;
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Id = bracketLinkId, Leg = leg }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<int> MarkResolvedAsync(long bracketLinkId, DateTime resolvedAtUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.BracketLinks
            SET    Status = 'RESOLVED',
                   ResolvedAt = @ResolvedAt
            WHERE  BracketLinkId = @Id AND Status <> 'RESOLVED';
        """;
        await using var conn = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Id = bracketLinkId, ResolvedAt = resolvedAtUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
