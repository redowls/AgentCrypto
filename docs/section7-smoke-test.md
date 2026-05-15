# Section 7 — Risk Manager: Smoke Test Checklist

§8.5 risk gate is wired in front of the execution engine. This checklist
verifies every gate fires correctly in a 24h testnet dry-run and that the
audit trail (`dbo.RiskEvents`) lets a reviewer reconstruct each decision.

## Pre-flight (one-time)

- [ ] `dotnet build TradingBot.sln` succeeds with zero warnings.
- [ ] `dotnet test tests/TradingBot.Tests/TradingBot.Tests.csproj --filter FullyQualifiedName~TradingBot.Tests.Risk` is green.
- [ ] DbUp has applied migration `006_correlations.sql` (verify
      `dbo.Correlations` and `dbo.CorrelationClusters` exist).
- [ ] `appsettings.json` has the `Risk` section populated; defaults match §8.
- [ ] Quartz registered both `GapDetectionJob` and `CorrelationRefreshJob`
      (check startup log — both should appear).
- [ ] `AccountSnapshotHostedService` logs "starting; cadence=00:01:00" on boot.

## Hour 0 — Dry-run start

- [ ] Process boots; `/health/ready` returns 200.
- [ ] First `dbo.AccountSnapshots` row written within 90s for both `SPOT`
      and `UMFUT` (the 00:00 UTC anchor for daily-loss math).
- [ ] Manually trigger a synthetic signal (insert a `dbo.Signals` row with
      `Status='GENERATED'` and matching SL/TP) and watch the engine call
      `IRiskManager.ApproveAsync`. Decision logged at `Information`.

## During the run

Capture **at least 100 approved/rejected decisions**. The test passes when
**zero policy violations** are present in the manual audit.

For each gate, force at least one rejection by pre-staging the trigger:

| Gate | Pre-stage | Expected reject reason |
|------|-----------|------------------------|
| (a)  | Lower `Risk:DailyLossLimitPct` to `-0.001` after ~1h of equity drift | `DAILY_LOSS_LIMIT` |
| (b)  | Raise `Risk:MaxDrawdownHaltPct` to `-0.001` | `MAX_DRAWDOWN_HALT` |
| (c)  | Open 4 manual `dbo.Positions` rows | `MAX_CONCURRENT_POSITIONS` |
| (d)  | Set `CorrelationThreshold` to `0` so every pair clusters together; open one position | `CORRELATION_CLUSTER_OCCUPIED` |
| (e)  | Drop equity by 16% (configure HWM accordingly) | `MAX_DRAWDOWN_HALT` (gate b absorbs e in the halt case) |
| (j)  | Submit a signal with a tiny stop distance | log line shows `notional` clamped to ≤50% equity, decision still `APPROVE` |
| (k)  | Open positions totaling 195% gross; submit one more with 6% notional | `GROSS_EXPOSURE` |
| (l)  | Manually insert a hostile funding rate (mock `IFundingRateProvider`) | `FUNDING_RATE_HOSTILE` |
| Pre-gate | `kill-switch-toggle.ps1 trip` | `KILL_SWITCH_ACTIVE` |

## Hour 23 — Audit

```sql
SELECT EventType, Severity, COUNT(*) AS Hits, MIN(EventTime) AS First, MAX(EventTime) AS Last
FROM   dbo.RiskEvents
WHERE  EventTime > DATEADD(HOUR, -24, SYSUTCDATETIME())
GROUP  BY EventType, Severity
ORDER  BY Hits DESC;
```

- [ ] Every reject reason listed above appears at least once.
- [ ] No `Severity='ERROR'` rows are produced by the gate itself (only WARN
      for rejects, CRITICAL for `KILL_SWITCH_TRIPPED`).
- [ ] Approved decisions outnumber rejects by ≥3:1 (sanity check: the gate
      is not over-firing).

## Nightly correlation job

- [ ] At 02:00 UTC the `CorrelationRefreshJob` log line "asOf=… universe=N
      pairs=P clusters=C threshold=0.7" appears.
- [ ] `SELECT COUNT(*) FROM dbo.Correlations WHERE AsOf = '<today>'` returns
      `N*(N-1)/2`.
- [ ] `SELECT COUNT(DISTINCT Cluster) FROM dbo.CorrelationClusters WHERE AsOf = '<today>'`
      ≥ 1.

## Kill switch verification

- [ ] `kill-switch-toggle.ps1 trip "manual drill"` (or via SQL: `INSERT INTO
      dbo.RiskEvents (...) VALUES (...)` and call `IKillSwitch.TripAsync`).
- [ ] All new `ApproveAsync` calls reject with `KILL_SWITCH_ACTIVE`.
- [ ] CRITICAL alert dispatched (verified once S11 is wired; for S7 it logs
      to Serilog at level `Critical`).
- [ ] `kill-switch-toggle.ps1 reset "post-drill"` clears the flag and writes
      a `KILL_SWITCH_RESET` audit row.

## Definition of Done sign-off

- [ ] All unit tests pass (`Risk` namespace = 527 tests).
- [ ] 24h testnet dry-run logged ≥ 100 decisions covering at least 8 of the
      9 gates above.
- [ ] Manual audit shows **zero** policy violations.
- [ ] Migration 006 ran clean against a fresh DB (re-run idempotency verified).
