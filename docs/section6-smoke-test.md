# Section 6 — Strategy Modules: Smoke-Test Checklist

Use after wiring §6 into a fresh testnet environment. Each step lists the
**action** and the **expected observation**. Stop at the first red item — do not
move on to §7 (AI confirmation) until every check below is green.

## 0. Preconditions
- [ ] §1–§5 deployed and green: SQL Server reachable, `dbo.Symbols` populated,
      `MarketDataIngestor` + `CandlePersistor` running, `IndicatorPreCacheService`
      writing snapshots to Redis (or in-memory cache).
- [ ] `Bot:Symbols` includes at least one of `BTCUSDT`, `ETHUSDT`, `SOLUSDT`.
- [ ] `MarketData:Subscriptions` includes the **strategies' primary timeframes**:
      `15m` for MR_BB_VWAP, `1h` for BREAKOUT_DON and TREND_EMA_ADX, plus `4h`
      for the TREND_EMA_ADX HTF filter.
- [ ] `Binance:UseTestnet=true` (smoke is testnet-only — no real-money exposure).

## 1. Build & unit tests
- [ ] `dotnet build TradingBot.sln -c Debug` ⇒ 0 warnings, 0 errors.
- [ ] `dotnet test tests/TradingBot.Tests --filter "FullyQualifiedName~Strategies"`
      ⇒ all green (golden tests, BracketCalculator properties, selector mapping,
      regression).
- [ ] `SignalEngineRegression.json` exists in `tests/TradingBot.Tests/Strategies/`
      and is non-empty (committed alongside the test).

## 2. Composition root
- [ ] `Program.cs` calls `AddStrategies(builder.Configuration)` (not the no-arg
      overload) so options bind from `appsettings.json`.
- [ ] On startup: `SignalEngine starting (channel cap=…)` line in console/Serilog.
- [ ] On startup: no `StrategySelector: no IStrategy registered for …` warnings
      (means all three strategies are wired).

## 3. Bar-close plumbing (S4 → S6)
Run for ≥ 5 minutes after warm-up, then:
- [ ] At least one `CandlePersistor: Persisted batch …` log line per minute on
      the busiest interval.
- [ ] At least one `SignalEngine: GENERATED …` *or* one `SignalEngine: regime=…
      no strategies active …` debug line per primary-TF bar close. Absence ⇒
      `IBarCloseChannel` not consumed (check DI registration).
- [ ] No `SignalEngine: error processing bar-close …` errors.

## 4. Indicator readiness
- [ ] `SignalEngine: no snapshot for …` lines disappear after the warm-up window
      (~200 bars on the slowest TF — for `1h` that's ~8 days; testnet smoke can
      shortcut this by replaying historical bars via `MarketDataIngestor`'s REST
      backfill).
- [ ] In Redis (or in-memory cache), `ind:BTCUSDT:1h` returns a snapshot with
      `Atr14`, `Ema200`, `Adx14`, `BbWidth` all populated (curl test or
      `redis-cli HGET ind:BTCUSDT:1h …` if Redis-backed).

## 5. Regime classification
- [ ] At least one row in `dbo.Regimes` per bar close after warm-up (if S5
      persists; otherwise check Serilog for `Classify` outputs from the engine).
- [ ] Regime distribution is plausible — not 100% UNKNOWN, not 100% one regime.
      Live BTC over 24h should produce a mix of TRENDING_*, RANGING, and at
      least one VOLATILE event.

## 6. Strategy gating (DEBUG-level)
Set `Serilog:MinimumLevel:Override:TradingBot.Strategies` = `Debug` and tail the
log for one full hour:
- [ ] `BREAKOUT_DON … gates: regime=… longBreak=… …` lines emitted on every
      eligible 1h bar — confirms the gate logger fires (per spec: "Logging at
      DEBUG includes every gating condition's boolean").
- [ ] Same for `MR_BB_VWAP … gates: …` on 15m bars in RANGING.
- [ ] Same for `TREND_EMA_ADX … gates: …` on 1h bars in TRENDING_*.

## 7. Signal persistence
- [ ] After ≥ 24h on testnet across BTC/ETH/SOL, at least one strategy produces
      ≥ 1 row in `dbo.Signals` with `Status='GENERATED'`.
- [ ] Every persisted row has non-null `EntryPrice`, `StopLoss`, `TakeProfit`,
      `AtrValue`, and `Regime`.
- [ ] For BUY signals: `StopLoss < EntryPrice < TakeProfit`. For SELL signals:
      `StopLoss > EntryPrice > TakeProfit`. Spot-check 5 rows.
- [ ] `Strategy` column is one of `BREAKOUT_DON`, `MR_BB_VWAP`, `TREND_EMA_ADX`
      (no typos, no NULLs).

## 8. Bracket math sanity (live row spot-check)
For a freshly persisted row, recompute and compare:
- [ ] BREAKOUT_DON: `|EntryPrice − StopLoss| ≈ 1.5 × AtrValue` (or × 1.2/0.8 if
      vol-adjusted — adjustment is on TP only, SL is unchanged).
- [ ] MR_BB_VWAP: `|EntryPrice − StopLoss| ≈ 1.0 × AtrValue`.
- [ ] TREND_EMA_ADX: `|EntryPrice − StopLoss| ≈ 2.0 × AtrValue`.
- [ ] Risk-reward (`|TP − Entry| / |Entry − SL|`) within ±20% of the §4.3 table
      target (1:2, 1:1.5, 1:2.5 respectively) — the ±20% covers the volatility
      adjustment.

## 9. Downstream channel
- [ ] At least one downstream consumer (S7 AI confirmer or S8 risk gate) drains
      `IGeneratedSignalChannel`. Verify by tailing for `GeneratedSignalChannel
      closed; signal … not forwarded` warnings — should be **zero** under normal
      operation.
- [ ] Channel `CurrentCount` (exposed via the diagnostic if registered) stays
      bounded — a steadily-rising count means a downstream consumer is stuck.

## 10. Selector × regime spot check
- [ ] Manually trigger a known regime (e.g., wait for / replay a high-ADX
      uptrend bar on `1h`). Verify TREND_EMA_ADX *and* BREAKOUT_DON both get
      a chance to fire (Serilog should show two `gates: …` lines for the same
      bar).
- [ ] During a quiet RANGING window, only MR_BB_VWAP gates lines appear; trend
      and breakout are silent on that bar.
- [ ] During COMPRESSING regime, no `gates: …` lines fire — only one debug line
      `regime=COMPRESSING … no strategies active …`.

## 11. Failure modes
- [ ] Disable a strategy via `Strategies:BreakoutDonchian:Enabled=false` and
      restart. Verify only the remaining two strategies emit gates lines.
- [ ] Stop the SQL Server temporarily; engine logs `persist failed for …` and
      keeps running (does not crash). Restart SQL; new signals persist on the
      next bar.

## 12. 24-hour acceptance
- [ ] After **24 hours** on testnet across BTC/ETH/SOL with §6 enabled:
      - [ ] At least one `Status='GENERATED'` row from any strategy.
      - [ ] No unhandled exceptions in `SignalEngine` logs.
      - [ ] Bar-close channel never reported `Wait` blocking persistor for
            > 1 second sustained (search for `back-pressure` style log noise).

## 13. Sign-off
- [ ] Print `SELECT Strategy, Side, Regime, COUNT(*) FROM dbo.Signals WHERE
      CreatedAt > DATEADD(hour, -24, GETUTCDATE()) GROUP BY Strategy, Side,
      Regime ORDER BY 4 DESC` and attach to the smoke-test PR.
- [ ] §6 owner signs off. §7 (AI confirmation) may now begin consuming
      `IGeneratedSignalChannel`.
