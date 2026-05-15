using Dapper;
using TradingBot.Backtest.Domain;
using TradingBot.Data.Connection;

namespace TradingBot.Backtest.Repositories;

// Read/write `dbo.BacktestRuns`. The run row is inserted PENDING at the start
// of each `bt run|wfa|mc` invocation, transitioned to RUNNING, then COMPLETED
// (or FAILED) with the final metrics blob.
internal sealed class BacktestRunRepository
{
    private readonly IDbConnectionFactory _factory;

    public BacktestRunRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(BacktestRun run, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.BacktestRuns
              (RunKind, ParentRunId, Strategy, Symbols, AccountType,
               FromUtc, ToUtc, StartingEquityUsd, Seed, ParametersJson,
               FeeMakerBps, FeeTakerBps, SlippageModelVersion, Status,
               StartedAt, Notes)
            OUTPUT INSERTED.BacktestRunId
            VALUES
              (@RunKind, @ParentRunId, @Strategy, @Symbols, @AccountType,
               @FromUtc, @ToUtc, @StartingEquityUsd, @Seed, @ParametersJson,
               @FeeMakerBps, @FeeTakerBps, @SlippageModelVersion, @Status,
               @StartedAt, @Notes);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, run, cancellationToken: ct))
            .ConfigureAwait(false);
        run.BacktestRunId = id;
        return id;
    }

    public async Task UpdateStatusAsync(long runId, string status, CancellationToken ct)
    {
        const string sql = "UPDATE dbo.BacktestRuns SET Status = @Status WHERE BacktestRunId = @Id;";
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = runId, Status = status }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task FinalizeAsync(
        long runId,
        string status,
        DateTime completedAtUtc,
        long durationMs,
        long? barsReplayed,
        int? tradesGenerated,
        decimal? finalEquityUsd,
        string? metricsJson,
        string? errorMessage,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.BacktestRuns
            SET    Status          = @Status,
                   CompletedAt     = @CompletedAt,
                   DurationMs      = @DurationMs,
                   BarsReplayed    = @BarsReplayed,
                   TradesGenerated = @TradesGenerated,
                   FinalEquityUsd  = @FinalEquityUsd,
                   MetricsJson     = @MetricsJson,
                   ErrorMessage    = @ErrorMessage
            WHERE  BacktestRunId   = @Id;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id              = runId,
            Status          = status,
            CompletedAt     = completedAtUtc,
            DurationMs      = durationMs,
            BarsReplayed    = barsReplayed,
            TradesGenerated = tradesGenerated,
            FinalEquityUsd  = finalEquityUsd,
            MetricsJson     = metricsJson,
            ErrorMessage    = errorMessage,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<BacktestRun?> GetByIdAsync(long runId, CancellationToken ct)
    {
        const string sql = """
            SELECT BacktestRunId, RunKind, ParentRunId, Strategy, Symbols, AccountType,
                   FromUtc, ToUtc, StartingEquityUsd, Seed, ParametersJson,
                   FeeMakerBps, FeeTakerBps, SlippageModelVersion, Status,
                   StartedAt, CompletedAt, DurationMs, BarsReplayed, TradesGenerated,
                   FinalEquityUsd, MetricsJson, ErrorMessage, Notes
            FROM   dbo.BacktestRuns WHERE BacktestRunId = @Id;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<BacktestRun>(
            new CommandDefinition(sql, new { Id = runId }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
