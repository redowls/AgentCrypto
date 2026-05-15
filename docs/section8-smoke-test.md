# Section 8 — Execution Engine: Smoke Test Checklist

§6 execution engine + §6.4 reconciliation are wired in front of live testnet
orders. This checklist drives a 24h dry-run that exercises every state in the
order machine, both bracket strategies (spot OCO + emulated futures), the
trailing-stop loop, the reconciliation drift sweep, and a chaos test that
validates idempotency under process kill.

## Pre-flight

- [ ] `dotnet build TradingBot.sln` succeeds with zero warnings.
- [ ] `dotnet test tests/TradingBot.Tests --filter "FullyQualifiedName~Execution"`
      → all green (90 tests at time of writing).
- [ ] DbUp has applied `007_exec_diag.sql` (verify `dbo.ExecutionDiagnostics`
      and `dbo.BracketLinks` exist; both have indices on `(SymbolId,RecordedAt)`
      and `(StopClientOrderId)/(TpClientOrderId)` respectively).
- [ ] `appsettings.json` has the `Execution` section populated; defaults match
      §6 (`ReconciliationInterval=00:00:30`, `NonTerminalAge=00:01:00`,
      `DriftAlertPctOfQty=0.005`, `DriftTripUsd=5.00`,
      `EnableSpotNativeOco=true`, time-stop ladder 6 / 4 / 12 bars).
- [ ] Worker `Program.cs` calls `services.AddExecution(builder.Configuration)`
      (the no-arg overload now throws — call site must pass IConfiguration).
- [ ] Hosted services on startup banner:
      `SignalApprovalHostedService`, `ExecutionEngine`, `UserDataReactor`,
      `TrailingStopManager`, `ReconciliationService`. All five must log a
      "starting" line in the first 5 seconds.
- [ ] `Binance:UseTestnet=true` (smoke is testnet-only).

## Hour 0 — Boot

- [ ] `/health/ready` returns 200.
- [ ] User-data WS connections established for both SPOT and UMFUT (tail for
      `Subscribed userData spot.userData.…` and `…fut.userData.…`).
- [ ] No `ExecutionEngine.SubmitAsync threw unexpectedly` in the first 60s.

## 1. Idempotent submission (manual injection)

Insert a synthetic approved intent by writing a row to `dbo.Signals` with
`Status=GENERATED` and pushing a `GeneratedSignalEvent` via a tools script,
or just publish to `IGeneratedSignalChannel` in a debug REPL.

- [ ] Submit twice in rapid succession (within 100ms). Verify exactly **one**
      `dbo.Orders` row is created (idempotent ClientOrderId), exactly **one**
      `binance.spot.placeOrder` log line.
- [ ] The clientOrderId matches the regex `^BOT-[A-Za-z0-9-]+-\d+-[A-Z0-9]+$`
      and is ≤36 chars.
- [ ] Order row transitions PENDING → SUBMITTING → NEW within 5s.

## 2. Full-fill happy path

- [ ] Inject a market-order intent. Within 5s observe in the logs:
      - `Spot PLACE op corr=…` (gateway)
      - `Order ACK corr=…` from ExecutionEngine
      - WS event: `OnUserEvent kind=OrderUpdate status=FILLED`
      - `Spot OCO placed` (bracket placement)
- [ ] `dbo.Orders.Status='FILLED'`, `FilledQty=Quantity`, `AvgFillPrice` set.
- [ ] `dbo.Fills` has at least one row with the correct `OrderId`.
- [ ] `dbo.Positions` row created with `Status='OPEN'` and `EntrySignalId`
      pointing at the injected signal.
- [ ] `dbo.BracketLinks` row inserted with `Status='ACTIVE'` and both
      `StopClientOrderId`/`TpClientOrderId` populated.
- [ ] `dbo.ExecutionDiagnostics` has one row with `ModelVersion='v1'` and
      `ObservedSlippageBps` ≈ delta(actual, signal.EntryPrice).

## 3. Partial-fill scenario

Inject a limit order at-the-touch with a quantity that exceeds top-of-book
(forces a partial fill before completion).

- [ ] Multiple `dbo.Fills` rows with cumulative `Quantity = order.FilledQty`.
- [ ] Order row transitions through PARTIALLY_FILLED (possibly multiple
      times — re-entrant transitions are legal) before reaching FILLED.
- [ ] No state-machine violations: tail for
      `state machine refused transition` warnings — should be zero.

## 4. Cancel race / partial → cancel

- [ ] Submit a limit-IOC order (or partial-fill scenario above) and
      simultaneously call cancel via `kill-switch-toggle.ps1` or via the
      gateway directly.
- [ ] Order ends in `CANCELED` with non-zero `FilledQty`.
- [ ] `dbo.Positions` row created with `Quantity = FilledQty` (reduced size).
- [ ] No double-position: query
      `SELECT COUNT(*) FROM dbo.Positions WHERE EntryOrderId=@OrderId` → 1.

## 5. Bracket placement — spot native OCO

- [ ] After a SPOT entry FILLED, observe `spot.placeOco` log line.
- [ ] Two child rows in `dbo.Orders` (TP-limit-maker + STOP_MARKET) sharing
      the same `SignalId`, both `Status='NEW'`.
- [ ] Manually trigger one leg (e.g. cancel one side via Binance UI). The
      sibling auto-cancels at the exchange and the BracketLink row flips to
      `Status='RESOLVED'` within one userData event cycle.

## 6. Bracket placement — futures emulated

Run with `Binance:EnableUsdmFutures=true` and inject a futures intent.

- [ ] Two ORDER_TRADE_UPDATE events: one STOP_MARKET reduceOnly, one
      TAKE_PROFIT_MARKET reduceOnly.
- [ ] When one leg fills, the userData reactor calls
      `IBracketLinkRepository.TryReserveSiblingCancelAsync` (atomic CAS).
      Verify `dbo.BracketLinks.ReservedSibling` flips from NULL to 'SL' or
      'TP' before the cancel call lands.
- [ ] The sibling order is cancelled exactly once (search for two
      `Fut CANCEL` lines for the same cid → must be impossible).
- [ ] BracketLink → `Status='RESOLVED'`, `ResolvedAt` populated.

## 7. Trailing stop update on bar close

- [ ] Wait for at least one `1h` bar close on a symbol with an open BREAKOUT
      position (or run a fast-forward replay via the MarketData backfill).
- [ ] Verify in logs: `Trail updated pos=… side=LONG oldSl=X newSl=Y seq=1`
      with `Y > X` (long) or `Y < X` (short).
- [ ] A new `dbo.Orders` row exists for the new SL with clientOrderId
      `BOT-TR001-…` and `Status='NEW'`.
- [ ] The previous SL row is `Status='CANCELED'`, Notes contains
      `replaced-by:BOT-TR001-…`.
- [ ] `dbo.BracketLinks` has a new ACTIVE row pointing at the new SL; the
      prior row is RESOLVED.
- [ ] `dbo.Positions.StopLoss` updated to the new value.

## 8. Trend partial-take

- [ ] Open a TREND_EMA_ADX position. When the price crosses
      `entry + 2 * (entry - SL)`, observe:
      - `Trend partial take fired pos=… qty=…` log line
      - A reduceOnly market order with `Quantity = position * 0.5`
      - `dbo.Positions.Quantity` reduced by 50%
- [ ] The partial take fires exactly once per position (in-memory tracker;
      restart of the bot will re-arm and could double-take — flag this in
      the runbook for ops awareness).

## 9. Time-stop

- [ ] Open an MR_BB_VWAP position on `15m`. After 6 bars (90 min) without
      reaching +1R progress, observe:
      - `Time-stop exit fired pos=… reason=TIME` log line
      - A reduceOnly market order for the full position quantity
      - Position closes via the userData reactor's bracket-leg path
- [ ] BREAKOUT positions hit time-stop at 4 bars on 1h (4h holding).
- [ ] TREND positions hit time-stop at 12 bars on 1h (12h holding).

## 10. Reconciliation — orphan orders

Stop the bot mid-submission (Ctrl-C between PENDING and SUBMITTING). Restart.

- [ ] On boot, `ReconciliationService starting (interval=00:00:30)` log line.
- [ ] Within 60s, `Reconciliation: N stale orders older than …` with N > 0.
- [ ] For each stale order, exactly one `Reconciliation GET /order` call.
- [ ] If exchange has the order: state transitions to whatever Binance
      reports (`Reconciliation applied cid=… NEW→FILLED`).
- [ ] If exchange has no record: row transitions to ERROR with
      Notes='reconciliation:no-such-order'.
- [ ] The bot does NOT re-submit any of these orders (search for duplicate
      `Spot PLACE` lines for the same clientOrderId — must be zero).

## 11. Reconciliation — drift sweep

- [ ] Manually flip a position's quantity in `dbo.Positions` to half the
      true exchange balance. Within 30s observe a `POSITION_DRIFT` row in
      `dbo.RiskEvents` with `Severity='WARN'`.
- [ ] Now flip it so that the drift exceeds `Risk:ReconciliationDriftTripUsd`
      ($5 by default). Within 30s observe:
      - `dbo.RiskEvents` row with `Severity='CRITICAL'`
      - Kill-switch tripped (`KillSwitch.IsTripped=true`); subsequent
        `IRiskManager.ApproveAsync` calls reject with `KILL_SWITCH_ACTIVE`
        (gate (a) of the §7 risk gate).
      - All new entries blocked; reduceOnly exits still permitted.

## 12. Chaos — kill during submission

```
# In one terminal:
dotnet run --project src/TradingBot.Worker

# In another, after a signal is approved and PENDING:
taskkill /IM dotnet.exe /F
```

- [ ] Restart the bot.
- [ ] Reconciliation discovers the orphan within 60s and either:
      - confirms the order existed at Binance (state = NEW/FILLED), OR
      - marks it ERROR (`no-such-order`) so the operator can manually
        decide whether to re-arm.
- [ ] No double-fill: `SELECT SUM(Quantity) FROM dbo.Fills WHERE OrderId=@X`
      matches the exchange-reported `executedQty` exactly.
- [ ] No duplicate ClientOrderId errors at Binance (which would be visible
      as `-2010 NEW_ORDER_REJECTED` in the logs).

## 13. State-machine audit

End-of-day SQL — every order must be in a legal terminal state:

```sql
SELECT Status, COUNT(*) AS N
FROM   dbo.Orders
WHERE  SubmittedAt > DATEADD(HOUR, -24, SYSUTCDATETIME())
GROUP  BY Status
ORDER  BY N DESC;
```

- [ ] Every status appears in `{PENDING, SUBMITTING, NEW, PARTIALLY_FILLED,
      FILLED, CANCELING, CANCELED, REJECTED, EXPIRED, ERROR}` — no rogues.
- [ ] After a 5-minute idle window, `PENDING` and `SUBMITTING` counts → 0
      (reconciliation drains them).

## 14. Slippage diagnostics

```sql
SELECT TOP 50 OrderId, Side, ExpectedSlippageBps, ObservedSlippageBps,
              ObservedSlippageBps - ExpectedSlippageBps AS Drift
FROM   dbo.ExecutionDiagnostics
ORDER  BY RecordedAt DESC;
```

- [ ] `ModelVersion = 'v1'` for every row (the active default model).
- [ ] Mean drift across 100 rows is within ±5bp (the model is calibrated
      against testnet conditions; live tuning is a future-work item).

## 15. End-to-end (Definition of Done)

The §8 DoD is a single integration scenario:

> **A manually-injected signal flows through to a filled position with a
> bracket and a trailing stop verified to update on bar close.**

To validate:

1. [ ] Insert a `dbo.Signals` row with `Status='GENERATED'` for BTCUSDT.
2. [ ] Push a `GeneratedSignalEvent` to the channel (debug REPL or test fixture).
3. [ ] Observe in order:
   - SignalApprovalHostedService: `Intent queued corr=…`
   - ExecutionEngine: `Order ACK corr=… status=FILLED`
   - UserDataReactor: position open + bracket placement
   - TrailingStopManager (after one bar close): `Trail updated pos=…`
4. [ ] After the trail update, manually trigger a SL hit (move price). Verify:
   - userData event delivers FILLED on the SL leg
   - sibling TP cancelled exactly once (futures) or auto-cancelled by OCO (spot)
   - `dbo.Positions.Status='CLOSED'`, `RealizedPnlUsd` populated.

## 16. Sign-off

- [ ] All checks above green.
- [ ] No `Severity='ERROR'` rows produced by the engine itself in the
      24h `dbo.RiskEvents` window.
- [ ] §8 owner signs off. §9 (operations / scheduled jobs) may now proceed.
