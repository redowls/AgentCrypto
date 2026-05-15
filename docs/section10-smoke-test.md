# Section 10 — Backtester + Walk-Forward + Monte Carlo smoke test

This doc walks through verifying §S10 end-to-end. Section 10 introduces the
deterministic replay engine in `src/TradingBot.Backtest`, the `bt` SQL schema
for parallel backtest output, and the `bt run | wfa | mc` CLI.

> **Status note (foundation pass).** This first cut delivers a working
> deterministic replay engine, full bt-schema persistence, all four headline
> metrics (Sharpe / Sortino / Calmar / DSR + MDD / PF / win-rate), the
> equity & drawdown CSVs, the markdown + JSON report, and the WFA / MC
> orchestrators. **Deferred to the next §S10 turn:** unit tests
> (buy-and-hold parity, replay determinism, lookahead-bias defence),
> per-fold parameter grid search, and a configurable WFA-config JSON file
> shape. The verdict + Sharpe ratio comparison + 70 % acceptance gate is
> already present and correct.

## Prerequisites

| Requirement | How to provide |
|---|---|
| LocalDB / SQL Server (Developer Edition or Express) | See `README.md` |
| `Database__ConnectionString` env var **or** `ConnectionStrings__TradingDb` | The CLI reads either. Same connection string the Worker uses. |
| Candle history in `dbo.Candles` for the symbol + window | Populate by running the Worker (S4 ingest) in advance, or run a one-off backfill. |
| `dbo.Symbols` row for `BTCUSDT` (BINANCE_SPOT) | Auto-seeded by `004_seed_symbols.sql`. |

The backtester targets the **same database** as live; backtest rows live
under the `bt.*` schema and `dbo.BacktestRuns / dbo.WalkForwardFolds /
dbo.MonteCarloResults`. Live `dbo.Orders / dbo.Positions / dbo.TradeHistory`
are never touched.

## 1. Migration 009 lands cleanly

```powershell
./Make-DevDb.ps1
```

Verify the new schema + tables exist:

```sql
SELECT SCHEMA_NAME(schema_id) AS sch, name
FROM   sys.tables
WHERE  name IN (
    'BacktestRuns', 'WalkForwardFolds', 'MonteCarloResults',
    'Signals', 'Orders', 'Fills', 'Positions', 'TradeHistory', 'AccountSnapshots')
  AND  SCHEMA_NAME(schema_id) IN ('dbo', 'bt')
ORDER BY sch, name;
```

You should see:

```
bt   AccountSnapshots
bt   Fills
bt   Orders
bt   Positions
bt   Signals
bt   TradeHistory
dbo  BacktestRuns
dbo  WalkForwardFolds
dbo  MonteCarloResults
dbo  ...      (live tables — same names as live)
```

## 2. Build the solution (zero warnings expected)

```powershell
dotnet build
```

`TradingBot.Backtest` builds as an `Exe` project — `dotnet run --project src/TradingBot.Backtest`
is its primary entry point.

## 3. Single backtest run end-to-end

```powershell
dotnet run --project src/TradingBot.Backtest -- run `
    --strategy TREND_EMA_ADX `
    --symbol   BTCUSDT `
    --from     2024-01-01 `
    --to       2026-01-01 `
    --notes    "S10 smoke test"
```

Expected stdout (last line):

```
Backtest run #N completed. Reports in: backtest-output/run-00000001/
```

Expected files in `backtest-output/run-00000001/`:

```
equity.csv              # one row per replayed bar
drawdown.csv            # equity-curve drawdown trace
metrics.json            # full metrics blob
report.md               # human-readable headline report
```

Open `report.md` and confirm it contains a configuration block and the
`## Headline metrics` table with non-zero values for Sharpe, MaxDrawdown,
Calmar, etc.

Verify rows landed in the bt schema:

```sql
DECLARE @RunId BIGINT = (SELECT MAX(BacktestRunId) FROM dbo.BacktestRuns);

SELECT TOP 5 * FROM dbo.BacktestRuns      WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS signals      FROM bt.Signals          WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS orders       FROM bt.Orders           WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS fills        FROM bt.Fills            WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS positions    FROM bt.Positions        WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS trades       FROM bt.TradeHistory     WHERE BacktestRunId = @RunId;
SELECT COUNT(*) AS snapshots    FROM bt.AccountSnapshots WHERE BacktestRunId = @RunId;
```

`signals ≈ trades` (each closed signal becomes a trade row, less any final
manual-close trade for a still-open position at end of window).

## 4. All three strategies on BTCUSDT 2024-01-01 → 2026-01-01

```powershell
foreach ($s in 'BREAKOUT_DON','MR_BB_VWAP','TREND_EMA_ADX') {
    dotnet run --project src/TradingBot.Backtest -- run `
        --strategy $s --symbol BTCUSDT `
        --from 2024-01-01 --to 2026-01-01 `
        --notes "smoke-$s"
}
```

Three new `dbo.BacktestRuns` rows; three `backtest-output/run-*/` directories
with reports. Compare the headline metrics across the three strategies in
the markdown reports.

## 5. Walk-forward analysis

```powershell
dotnet run --project src/TradingBot.Backtest -- wfa `
    --strategy   TREND_EMA_ADX `
    --symbol     BTCUSDT `
    --from       2024-01-01 `
    --to         2026-01-01 `
    --is-months  6 `
    --oos-months 1 `
    --step-months 1
```

Expected stdout:

```
WFA parent run #N completed. See dbo.WalkForwardFolds for verdict.
```

Verify the per-fold verdict:

```sql
DECLARE @ParentId BIGINT = (
    SELECT MAX(BacktestRunId) FROM dbo.BacktestRuns WHERE RunKind = 'WFA'
);

SELECT FoldIndex, IsFromUtc, IsToUtc, OosFromUtc, OosToUtc,
       IsSharpe, OosSharpe, AcceptanceMet, IsTradeCount, OosTradeCount
FROM   dbo.WalkForwardFolds
WHERE  ParentRunId = @ParentId
ORDER  BY FoldIndex;

SELECT MetricsJson FROM dbo.BacktestRuns WHERE BacktestRunId = @ParentId;
```

The parent's `MetricsJson` includes `folds`, `folds_passed`, `folds_pass_pct`,
and the verdict (`ACCEPT` if ≥ 70 % of folds satisfy `OOS Sharpe ≥ 0.6 × IS Sharpe`).

## 6. Monte Carlo on a completed run

```powershell
dotnet run --project src/TradingBot.Backtest -- mc `
    --runId  1 `        # an existing dbo.BacktestRuns.BacktestRunId from step 3
    --reshuffles 1000 `
    --skips      100
```

Expected log (last line):

```
MC parent #1: reshuffle MDD p5/p50/p95 = X/Y/Z%, skip MDD p5/p50/p95 = X/Y/Z% — verdict ACCEPT (95th < 25%)
```

Inspect the per-iteration rows:

```sql
SELECT SimulationKind, COUNT(*) AS n,
       MIN(MaxDrawdownPct) AS min_mdd,
       AVG(MaxDrawdownPct) AS avg_mdd,
       MAX(MaxDrawdownPct) AS max_mdd
FROM   dbo.MonteCarloResults
WHERE  ParentRunId = 1
GROUP  BY SimulationKind;
```

## 7. Definition-of-Done checklist

- [ ] Migration 009 applies cleanly via `./Make-DevDb.ps1`.
- [ ] `dotnet build` is warning-free.
- [ ] `bt run` produces a `dbo.BacktestRuns` row, populates `bt.*`, writes
      the four output files, and ends in `Status = 'COMPLETED'`.
- [ ] All three strategies run to completion on the 2024–2026 window.
- [ ] `bt wfa` emits a per-fold table in `dbo.WalkForwardFolds` and a verdict
      JSON on the parent row.
- [ ] `bt mc` emits ≥ 1,100 rows in `dbo.MonteCarloResults` (1,000 reshuffles +
      100 skips) and the runner logs the 5/50/95 percentiles.

## 8. What to check next (deferred to follow-up §S10 turn)

- **Determinism test.** Two consecutive `bt run` invocations with the same
  seed and inputs should produce byte-identical `equity.csv` /
  `report.md`. To be added as `tests/TradingBot.Tests/Backtest/DeterminismTests.cs`.
- **Buy-and-hold parity.** A no-op strategy that just holds at start should
  produce metrics matching a hand-calculated `(close[end] / close[start]) - 1`.
- **Lookahead defence.** Verify strategies cannot read a candle whose
  `OpenTime > replayClock`. Black-box test driving a deliberately
  forward-shifted candle and asserting it's never used.
- **Per-fold parameter grid.** WFA currently runs the default parameter
  set per fold. v2: read a JSON grid from `--config`, optimise IS Sharpe
  on the grid, freeze the optimum into `BestParametersJson`, and evaluate
  unchanged on OOS.
- **ATR50-SMA in backtest sizing.** The vol-adjust factor currently
  defaults to 1.0× because the ATR50-SMA pre-cache isn't wired. Plumb in
  the `IIndicatorCache` warmup so the live `RiskMath.VolAdjust` reads
  match live behaviour.

## Common issues

- **"No candles in window"**: `dbo.Candles` is empty for the symbol /
  interval / window. Run the Worker first to ingest history, or backfill
  with the gap-detection job. The strategy's `PrimaryTimeframe`
  (`5m` / `15m` / `1h` depending on the strategy) is the interval the
  engine queries.
- **"Symbol 'X' not found"**: The symbol catalogue isn't seeded for
  `BINANCE_SPOT`. `004_seed_symbols.sql` seeds the standard list; reapply
  with `./Make-DevDb.ps1`.
- **Risk gate rejects every signal**: usually `RiskOptions:RiskPerTradeFraction`
  is mis-configured against the starting equity, producing a zero quantity
  after the lot-step clamp. Confirm `Backtest:StartingEquityUsd` is high
  enough for `qty * price ≥ MinNotional` (BTC at ~50 k USD with default
  1 % risk needs ≥ 1 000 USD).
