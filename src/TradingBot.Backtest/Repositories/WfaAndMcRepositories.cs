using Dapper;
using TradingBot.Data.Connection;

namespace TradingBot.Backtest.Repositories;

// Walk-forward folds (one row per fold, twin BacktestRun children for IS+OOS).
internal sealed class WalkForwardFoldRepository
{
    private readonly IDbConnectionFactory _factory;
    public WalkForwardFoldRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> InsertAsync(WfaFoldRow row, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.WalkForwardFolds
              (ParentRunId, FoldIndex, IsFromUtc, IsToUtc, OosFromUtc, OosToUtc,
               IsRunId, OosRunId, BestParametersJson)
            OUTPUT INSERTED.WfaFoldId
            VALUES
              (@ParentRunId, @FoldIndex, @IsFromUtc, @IsToUtc, @OosFromUtc, @OosToUtc,
               @IsRunId, @OosRunId, @BestParametersJson);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, row, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task UpdateMetricsAsync(WfaFoldMetricsUpdate u, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.WalkForwardFolds
            SET    IsSharpe      = @IsSharpe,
                   OosSharpe     = @OosSharpe,
                   IsCalmar      = @IsCalmar,
                   OosCalmar     = @OosCalmar,
                   IsMaxDdPct    = @IsMaxDdPct,
                   OosMaxDdPct   = @OosMaxDdPct,
                   IsTradeCount  = @IsTradeCount,
                   OosTradeCount = @OosTradeCount,
                   AcceptanceMet = @AcceptanceMet
            WHERE  WfaFoldId = @WfaFoldId;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, u, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}

internal sealed record WfaFoldRow(
    long ParentRunId, int FoldIndex,
    DateTime IsFromUtc, DateTime IsToUtc, DateTime OosFromUtc, DateTime OosToUtc,
    long IsRunId, long OosRunId, string? BestParametersJson);

internal sealed record WfaFoldMetricsUpdate(
    long WfaFoldId,
    decimal? IsSharpe, decimal? OosSharpe,
    decimal? IsCalmar, decimal? OosCalmar,
    decimal? IsMaxDdPct, decimal? OosMaxDdPct,
    int? IsTradeCount, int? OosTradeCount,
    bool? AcceptanceMet);

// Monte Carlo simulation result rows.
internal sealed class MonteCarloResultRepository
{
    private readonly IDbConnectionFactory _factory;
    public MonteCarloResultRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(McResultRow row, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.MonteCarloResults
              (ParentRunId, SimulationKind, Iteration, Seed, SkipFraction,
               FinalEquityUsd, MaxDrawdownPct, Sharpe, TradesUsed)
            VALUES
              (@ParentRunId, @SimulationKind, @Iteration, @Seed, @SkipFraction,
               @FinalEquityUsd, @MaxDrawdownPct, @Sharpe, @TradesUsed);
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<(decimal P5, decimal P50, decimal P95)> GetMddQuantilesAsync(
        long parentRunId, string kind, CancellationToken ct)
    {
        const string sql = """
            SELECT MaxDrawdownPct
            FROM   dbo.MonteCarloResults
            WHERE  ParentRunId = @RunId AND SimulationKind = @Kind
            ORDER  BY MaxDrawdownPct ASC;
        """;
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<decimal>(
            new CommandDefinition(sql, new { RunId = parentRunId, Kind = kind }, cancellationToken: ct))
            .ConfigureAwait(false)).AsList();
        if (rows.Count == 0) return (0m, 0m, 0m);
        return (Quantile(rows, 0.05), Quantile(rows, 0.50), Quantile(rows, 0.95));
    }

    private static decimal Quantile(IReadOnlyList<decimal> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        var pos = q * (sorted.Count - 1);
        var lo  = (int)Math.Floor(pos);
        var hi  = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        var frac = (decimal)(pos - lo);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}

internal sealed record McResultRow(
    long ParentRunId, string SimulationKind, int Iteration, long Seed,
    decimal? SkipFraction, decimal FinalEquityUsd, decimal MaxDrawdownPct,
    decimal? Sharpe, int TradesUsed);
