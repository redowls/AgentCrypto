# Claude Code Prompts — Binance AI-Assisted Trading System

This document contains:
1. **The Master Planning Prompt** (paste this FIRST into Claude Code along with the design `.md` file).
2. **12 Section-by-Section Build Prompts** (paste these one at a time, in order, after Claude Code confirms the plan).

---

## 🟢 PROMPT 0 — MASTER PLANNING PROMPT (paste this first)

```
You are an expert senior .NET / quant trading developer. I am attaching the full system design document for a Binance AI-Assisted Automated Trading System (Markdown). Your job in THIS message is NOT to write code yet — it is to plan the build.

REFERENCE DOCUMENT: [attach the design .md file]

YOUR TASK:
1. Read and internalize the entire design document.
2. Confirm you understand:
   - Stack: .NET 8 + SQL Server + Binance.Net + Skender.Stock.Indicators + Polly + Serilog + Quartz/Hangfire + Claude API + optional XGBoostSharp + n8n
   - Account profile: $10K–$50K, max DD 10–15%, max futures leverage 2–3×
   - Three strategies: BREAKOUT_DON, MR_BB_VWAP, TREND_EMA_ADX
   - Three phases: MVP (4–6w), Optimization (6–8w), Production (4–6w)
3. Produce a development breakdown with the following deliverables:

   A. PROJECT STRUCTURE
      - Solution layout (.sln + projects). Recommend project boundaries (e.g.,
        TradingBot.Core, TradingBot.Data, TradingBot.Exchange, TradingBot.Strategies,
        TradingBot.Risk, TradingBot.Execution, TradingBot.AI, TradingBot.Backtest,
        TradingBot.Worker, TradingBot.Tests).
      - Folder layout per project.
      - NuGet package list per project with current stable versions.

   B. BUILD ORDER (12 SECTIONS)
      For each of the 12 sections below, list:
      - Goal (1–2 sentences)
      - Inputs needed from me (API keys, credentials, config decisions)
      - Outputs produced (files, schemas, services)
      - Dependencies on previous sections
      - Definition of Done (testable criteria)
      - Estimated effort in hours

      The 12 sections are:
        S1.  Solution scaffolding + config + secrets management
        S2.  Database layer (DDL, EF Core or Dapper, migrations, partitioning)
        S3.  Binance integration (REST + WebSocket spot/futures, reference data)
        S4.  Candle ingestion + persistence (real-time + backfill)
        S5.  Indicator engine (Skender wiring, incremental updates, regime classifier)
        S6.  Strategy modules (BREAKOUT_DON, MR_BB_VWAP, TREND_EMA_ADX)
        S7.  Risk manager (sizing, caps, correlation, drawdown ladder, kill-switch)
        S8.  Execution engine (order state machine, Polly resilience, OCO/bracket emulation)
        S9.  AI layer (Claude API integration: sentiment, regime, confirmation, journal; caching)
        S10. Backtester + walk-forward + Monte Carlo
        S11. Logging, monitoring, alerting (Serilog→Seq, Prometheus, Telegram via n8n)
        S12. Deployment (Docker, Quartz schedules, DR, reconciliation, go-live checklist)

   C. CROSS-CUTTING CONCERNS
      - Configuration strategy (appsettings + environment + Key Vault)
      - Logging conventions (CorrelationId = SignalId)
      - Testing strategy (unit / integration / paper / shadow)
      - CI/CD pipeline outline
      - Branching/release strategy

   D. RISKS & ASSUMPTIONS
      - Top 10 technical risks with mitigation
      - Assumptions you are making about my environment

   E. WHAT I MUST DECIDE BEFORE WE START SECTION 1
      - Concrete questions you need answered (e.g., "Azure or AWS?", "SQL Server edition?", "Telegram bot token?")

CONSTRAINTS ON YOUR PLAN:
- Each section must be independently buildable and testable.
- No section may exceed ~8 hours of focused dev work; if larger, split it.
- Every section must end with a runnable demo / smoke test.
- Do NOT write any production code yet. Only the plan.

OUTPUT FORMAT:
- Markdown.
- Tables for the build order and effort estimates.
- A clear "READY FOR SECTION 1?" prompt at the end asking me to confirm or adjust the plan.

After I approve the plan, I will paste section-specific prompts one at a time. DO NOT start coding until I explicitly say "Begin Section 1".
```

---

## 🔵 SECTION-BY-SECTION BUILD PROMPTS

> Paste each prompt below into Claude Code only after the previous section is verified working. Each prompt is self-contained and references the design document.

---

### PROMPT S1 — Solution Scaffolding + Config + Secrets

```
Begin SECTION 1: Solution Scaffolding + Config + Secrets.

CONTEXT: Refer to the system design document already in this conversation, especially §1 (Architecture) and §9 (Deployment).

DELIVER:
1. A .NET 8 solution with these projects:
   - TradingBot.Core           (domain types, interfaces, enums)
   - TradingBot.Data           (EF Core DbContext OR Dapper repos — recommend and justify)
   - TradingBot.Exchange       (Binance.Net wrappers, reference data)
   - TradingBot.Strategies     (rule-based strategy modules)
   - TradingBot.Risk           (risk manager)
   - TradingBot.Execution      (order state machine, Polly pipeline)
   - TradingBot.AI             (Claude client, prompt templates, caching)
   - TradingBot.Backtest       (replay engine, walk-forward, Monte Carlo)
   - TradingBot.Worker         (Microsoft.Extensions.Hosting Worker Service host)
   - TradingBot.Tests          (xUnit + FluentAssertions + Moq)

2. Configuration layering:
   - appsettings.json (defaults)
   - appsettings.Development.json / appsettings.Production.json
   - Environment variables override
   - Azure Key Vault / AWS Secrets Manager hook (interface + dev fallback to user-secrets)
   - A typed `BotOptions` class with validation (DataAnnotations or FluentValidation)

3. Secrets handling:
   - Binance API key/secret loaded from Key Vault or user-secrets
   - Anthropic API key loaded same way
   - Telegram bot token same way
   - Production NEVER reads keys from disk-committed files

4. Serilog wired with Console + File + Seq sinks (Seq URL from config).
5. Health check endpoint at `/health` (return DB ping + Binance ping + WS connection state).
6. README.md with: prerequisites, how to run locally, how to set secrets.

CONSTRAINTS:
- Use .NET 8 LTS, C# 12.
- Use NuGet versions current as of today (state versions chosen).
- No business logic yet. Only scaffolding + DI plumbing.

OUTPUT:
- Full project tree (file paths)
- Each file's complete content
- A bash/PowerShell command list to create the solution from scratch (`dotnet new sln`, `dotnet new worker`, etc.)

DEFINITION OF DONE:
- `dotnet build` succeeds with zero warnings.
- `dotnet run --project TradingBot.Worker` starts, logs "Bot host started", and the `/health` endpoint returns 200.
- `dotnet test` runs (even if zero tests) with green.
- README explains how to set Binance testnet keys via `dotnet user-secrets`.

After implementing, give me a SMOKE TEST CHECKLIST I can run.
```

---

### PROMPT S2 — Database Layer

```
Begin SECTION 2: Database Layer.

CONTEXT: §2 of the design document (full DDL, partitioning, retention).

DELIVER:
1. SQL DDL scripts in `/sql/migrations`:
   - 001_init_schema.sql       (Symbols, Candles, Signals, Orders, Fills, Positions,
                                TradeHistory, AccountSnapshots, RiskEvents, AiInteractions)
   - 002_partition_function.sql (pf_CandleMonth + ps_CandleMonth)
   - 003_indexes.sql            (all non-clustered indexes from §2.4)
   - 004_seed_symbols.sql       (BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT for spot + UMFUT)

2. A migration runner. Recommend ONE of:
   - DbUp (lightweight, SQL-first)        ← preferred for this kind of project
   - EF Core migrations (if you chose EF in S1)
   Justify the choice.

3. Data access layer in TradingBot.Data:
   - If Dapper: typed repositories (ICandleRepository, ISignalRepository, IOrderRepository,
     IPositionRepository, ITradeHistoryRepository, IAccountSnapshotRepository,
     IRiskEventRepository, IAiInteractionRepository).
   - All write methods use `MERGE` or `INSERT ... WHERE NOT EXISTS` for idempotency
     (especially Candles upsert by (SymbolId, Interval, OpenTime)).
   - Bulk insert for candles via SqlBulkCopy (batch size 500).

4. Domain entity classes in TradingBot.Core matching the schema, using
   `decimal` for all price/qty fields and `DateTime` UTC.

5. Integration tests in TradingBot.Tests using Testcontainers-dotnet for SQL Server
   that:
   - Apply migrations to a fresh container
   - Insert + read + update a Candle, Signal, Order, Fill, Position
   - Verify partition was created
   - Verify unique constraint on ClientOrderId

6. A `Make-DevDb.ps1` (or bash equivalent) that creates a local TradingDb
   from scratch and runs all migrations.

CONSTRAINTS:
- All DateTime values UTC. All decimals DECIMAL(38,18) on prices/qty.
- No Entity Framework lazy loading.
- Repository methods accept CancellationToken.
- Bulk insert performance target: 10,000 candles/sec on dev laptop.

OUTPUT:
- Full SQL files
- Full repository code
- Full test code
- Run instructions

DEFINITION OF DONE:
- `Make-DevDb.ps1` creates schema cleanly twice in a row (idempotent).
- Integration tests pass.
- Bulk insert benchmark prints ≥10K rows/sec.

Provide a SMOKE TEST CHECKLIST when done.
```

---

### PROMPT S3 — Binance Integration

```
Begin SECTION 3: Binance Integration (REST + WebSocket, spot + USDⓈ-M futures).

CONTEXT: §6 (Execution), §9.7 (key safety), Appendix B (rate limits).

DELIVER (in TradingBot.Exchange):

1. `IBinanceGateway` interface abstracting:
   - GetExchangeInfoAsync(AccountType)              // returns symbol filters
   - GetKlinesAsync(symbol, interval, start, end)   // historical REST
   - PlaceOrderAsync(OrderRequest)
   - CancelOrderAsync(symbol, clientOrderId)
   - GetOrderAsync(symbol, clientOrderId)
   - GetOpenOrdersAsync(symbol?)
   - GetAccountAsync(AccountType)
   - GetUserTradesAsync(symbol, fromTradeId)
   - StartUserDataStreamAsync()
   - SubscribeKlineAsync(symbol, interval, callback)
   - SubscribeUserDataAsync(listenKey, callback)

2. Two implementations:
   - BinanceSpotGateway   (using Binance.Net Spot client)
   - BinanceFuturesGateway (using Binance.Net UM Futures client)
   Both must support BOTH testnet and mainnet selectable via config.

3. Reference data service:
   - Loads exchangeInfo on startup and daily at 00:05 UTC
   - Persists/refreshes Symbols rows (TickSize, StepSize, MinNotional)
   - Exposes ISymbolFilters for the execution engine to clamp price/qty

4. WebSocket manager:
   - Auto-reconnect with exponential backoff (already in Binance.Net but verify config)
   - listenKey keepalive every 30 minutes (PUT before 60-min expiry)
   - Health metric: timestamp of last received message per stream
   - Watchdog raises CRITICAL alert if no message for 60s on a subscribed stream

5. Polly v8 resilience pipeline wrapping all REST calls:
   - Retry on 5xx, -1001, -1003, -1015, -1021, network exceptions
   - Exponential backoff with decorrelated jitter
   - Circuit breaker: 50% failure / 8 reqs / 30s window / 1-min break
   - Timeout 8s per call
   - Special handler for 429 (respect retry-after) and 418 (kill-switch event)

6. Filter clamping helpers:
   - ClampPriceToTick(price, tickSize)
   - ClampQuantityToStep(qty, stepSize)
   - EnforceMinNotional(qty, price, minNotional) → throws if cannot satisfy
   Use Binance.Net's BinanceHelpers where available.

7. Tests:
   - Mocked unit tests for clamping helpers
   - Live testnet integration tests (gated by env var BINANCE_TESTNET=true) that:
     * fetch exchange info
     * fetch last 100 klines
     * place a tiny LIMIT order far from market, then cancel
     * subscribe to BTCUSDT kline_1m and assert at least one message in 90s

CONSTRAINTS:
- API keys from Key Vault / user-secrets, never logged.
- All log lines about orders include correlation id.
- DO NOT enable WITHDRAW permission — code must verify via account info that the key has trading-only permission and refuse to start otherwise.

OUTPUT:
- Full code for gateway, ref data, WS manager, Polly pipeline, helpers
- Tests
- Smoke test instructions for testnet

DEFINITION OF DONE:
- Smoke test on Binance Spot Testnet successfully places + cancels an order.
- WS receives kline updates for BTCUSDT for 5 minutes without disconnect.
- Reference data refresh stores ≥100 symbols in DB.
- Watchdog alert fires within 90s when WS is forcibly killed.
```

---

### PROMPT S4 — Candle Ingestion + Persistence

```
Begin SECTION 4: Candle Ingestion + Persistence.

CONTEXT: §1.2 data flow, §1.3 batch vs real-time, §2 schema.

DELIVER:

1. `MarketDataIngestor : BackgroundService` that, on startup:
   - Reads list of (Symbol, Interval) pairs from config
   - For each, performs REST backfill of the last N bars (default 500) using GetKlinesAsync
   - Subscribes to WS kline streams for the same pairs
   - Pushes every kline event onto an in-process Channel<KlineEvent>

2. `CandlePersistor : BackgroundService` consumer:
   - Reads from the channel
   - Buffers up to 500 rows or 2 seconds, whichever first
   - Bulk-upserts via SqlBulkCopy + MERGE (using S2 repository)
   - Only writes IsClosed=1 candles to the canonical table; in-progress
     bars go to a separate `LiveCandles` Redis hash

3. Gap detection job (Quartz, every 5 min):
   - For each (Symbol, Interval), find the latest stored OpenTime
   - If gap > 2 × interval, REST-backfill the missing range
   - Log `RiskEvents` row of severity WARN if gaps found

4. Indicator pre-cache:
   - On every closed bar, compute the strategy-required indicators
     (ATR14, EMA9/21/50/200, ADX14, RSI14, BB(20,2), Donchian(20), VWAP-session)
     and cache the latest values in Redis under `ind:{symbol}:{tf}`
   - Use Skender.Stock.Indicators

5. Tests:
   - Unit test the channel buffering logic (deterministic clock)
   - Integration test on testnet: subscribe BTCUSDT 5m for 20 minutes, verify
     ≥4 closed bars persisted, no gaps, indicators populated

CONSTRAINTS:
- Memory cap on the channel = 10,000 (bounded). Drop policy = block (back-pressure to WS).
- All writes idempotent.
- One persistor instance per process; protect with named-mutex if multiple instances configured.

DEFINITION OF DONE:
- Run for 1 hour on testnet on BTCUSDT 5m + 15m + 1h: SQL Candles table has
  exactly the expected number of rows; Redis indicator cache populated for all 3 TFs.
- Killing the process mid-run and restarting causes no duplicates and no gaps after backfill.

Provide SMOKE TEST CHECKLIST.
```

---

### PROMPT S5 — Indicator Engine + Regime Classifier

```
Begin SECTION 5: Indicator Engine + Regime Classifier.

CONTEXT: §3 (strategies), §1.1 (indicator engine component).

DELIVER (in TradingBot.Strategies/Indicators or TradingBot.Core/Indicators):

1. `IIndicatorEngine`:
   - GetSnapshotAsync(SymbolId, Interval, asOf) → IndicatorSnapshot
     (ATR14, EMA9/21/50/200, ADX14, +DI, -DI, RSI14, BBUpper/Mid/Lower/Width,
      DonchianUpper/Lower(20), VWAP, ATR50_SMA, BBWidth_50pctl)
   - Backed by Skender.Stock.Indicators; for hot path uses the Redis cache from S4

2. Higher-timeframe alignment helper:
   - GetHtfSnapshotAsync(SymbolId, "4h", asOf) for the trend strategy

3. `IRegimeClassifier`:
   - ClassifyAsync(IndicatorSnapshot) → Regime (TRENDING_UP, TRENDING_DOWN,
     RANGING, VOLATILE, COMPRESSING) with rule-based logic per §3.4:
       TRENDING:    ADX>25 AND BBW expanding
       RANGING:     ADX<20 AND BBW < 0.7 × SMA(BBW,50)
       VOLATILE:    ATR > 1.5 × ATR50_SMA AND ADX in [20,25]
       COMPRESSING: BBW low percentile AND ADX rising
   - Returns confidence 0..1

4. Regime persistence:
   - Every regime classification stored to a new table `Regimes`
     (RegimeId, SymbolId, Interval, AsOf, Regime, Confidence, Inputs JSON)
   - Provide migration 005_regimes.sql

5. Tests:
   - Synthetic candle series fixtures that produce each regime; assert classifier outputs
   - Performance test: classify 10,000 snapshots in <1s

CONSTRAINTS:
- Indicator math must match Skender library exactly; do not re-implement.
- Regime classifier is pure function (no I/O) — easy to unit test.
- AI-based regime confirmation is deferred to S9; for now rule-based only.

DEFINITION OF DONE:
- Unit tests pass with synthetic fixtures for all 5 regimes.
- Performance test passes.
- Live test on testnet shows regime updates per bar close in logs.
```

---

### PROMPT S6 — Strategy Modules

```
Begin SECTION 6: Strategy Modules.

CONTEXT: §3.1, §3.2, §3.3 (full entry/exit logic), §4 (SL/TP model).

DELIVER (in TradingBot.Strategies):

1. `IStrategy` interface:
   - string Name { get; }
   - Interval PrimaryTimeframe { get; }
   - Regime[] AllowedRegimes { get; }
   - SignalCandidate? Evaluate(IndicatorSnapshot snap, IndicatorSnapshot? htf, Regime regime, MarketContext ctx);

2. Three concrete strategies, EACH implementing the EXACT rules in the design doc:

   a. `BreakoutDonchianStrategy` — §3.1
      - 1h primary, uses Donchian(20), Volume×1.5 SMA20, EMA200, ADX>20
      - Initial SL = entry − 1.5×ATR; TP = entry + 3×ATR
      - Specifies trail config: ChandelierExit(22, 3) activates after +1.5R

   b. `MeanReversionBbVwapStrategy` — §3.2
      - 15m primary, RSI14 <25 / >75, BB(20,2), VWAP filter, ADX<25
      - SL = entry ± 1.0×ATR; TP1 = VWAP (50% off), TP2 = +1.5R
      - Time stop 8 bars

   c. `TrendEmaAdxStrategy` — §3.3
      - 1h primary + 4h HTF filter, EMA9/21 cross, EMA50 trend, EMA200_4h, ADX>25
      - SL = entry ± 2.0×ATR; partial 50% at +2R; runner trails EMA21 or 2×ATR Chandelier
      - Hard time stop 5 days

3. `BracketCalculator` static helper implementing §4.2 ATR-based bracket math
   with strategy-specific multipliers AND volatility adjustment per §4.2 final paragraph.

4. `StrategySelector`:
   - Given a regime, returns the active strategies and their size multipliers
     per §3.4 table (e.g., VOLATILE → BREAKOUT_DON @ 0.5×)

5. `SignalEngine : BackgroundService`:
   - Subscribes to bar-close events from S4
   - Pulls indicator snapshot from S5
   - Pulls regime from S5
   - Asks StrategySelector which strategies are eligible
   - Runs each eligible strategy's Evaluate
   - For every non-null candidate: persist to dbo.Signals with Status=GENERATED
     and forward to next stage (channel)

6. Tests:
   - Golden-test each strategy with hand-crafted candle fixtures for hit/miss conditions
   - Property-based tests for BracketCalculator (FsCheck or built-in)
   - End-to-end test: run engine on 1 month of historical BTC 1h candles,
     verify signals match a frozen expected JSON file (regression suite)

CONSTRAINTS:
- Strategies are PURE FUNCTIONS of the snapshot — no I/O, no DB calls inside Evaluate.
- All thresholds come from a per-strategy `IOptions<>` config block, not hard-coded.
- Logging at DEBUG includes every gating condition's boolean.

DEFINITION OF DONE:
- Unit + golden tests green.
- Live testnet run: at least one strategy produces ≥1 signal in 24h on BTC/ETH/SOL.
- All signals correctly persisted with EntryPrice/SL/TP.

Provide SMOKE TEST CHECKLIST.
```

---

### PROMPT S7 — Risk Manager

```
Begin SECTION 7: Risk Manager.

CONTEXT: §8 in full (sizing, caps, correlation, drawdown ladder, kill-switch).

DELIVER (in TradingBot.Risk):

1. `IRiskManager`:
   - Task<RiskDecision> ApproveAsync(Signal signal, AccountSnapshot acct, CancellationToken ct);
     RiskDecision = Approve(quantity) | Reject(reason)

2. Implementation per §8.5 pseudocode, with these gates IN ORDER:
   a. Daily loss limit (-3%) → reject DAILY_LOSS_LIMIT
   b. Max drawdown -15% from HWM → reject MAX_DRAWDOWN_HALT
   c. Open positions ≥4 → reject MAX_CONCURRENT_POSITIONS
   d. Correlation cluster check → reject CORRELATION_CLUSTER_OCCUPIED
   e. Drawdown ladder size factor (0/0.25/0.5/1.0)
   f. Vol-adjust factor based on ATR ratio
   g. Risk dollars = equity × 0.01 × kFactor × volAdjust
   h. Quantity = riskDollars / stopDistance
   i. Clamp to Binance lot/notional filters
   j. Single-symbol cap 50% equity
   k. Gross exposure cap 200% equity (futures)
   l. Funding-rate veto for futures (skip if hostile + we'd pay)

3. `IAccountSnapshotProvider`:
   - Real-time mark-to-market combining DB positions + Binance live prices
   - Tracks HWM in dbo.AccountSnapshots; computes DrawdownPct
   - Computes DailyPnlPct (since 00:00 UTC)

4. `ICorrelationService`:
   - Nightly Quartz job that pulls last 30 days of daily closes for the universe,
     computes return correlation matrix, persists to a new table `Correlations`
     (migration 006_correlations.sql)
   - Cluster assignment: simple greedy threshold > 0.7 grouping
   - Used by gate (d) to check if a cluster is already occupied on the same side

5. `KillSwitch`:
   - Global atomic flag in Redis + DB
   - Tripped by: HTTP 418 from Binance, daily loss limit, MAX_DRAWDOWN_HALT,
     manual command, reconciliation drift > $5
   - When tripped: RiskManager rejects all new entries; ExecutionEngine
     still allows reduceOnly exits; CRITICAL alert via S11

6. Tests:
   - Unit tests for each gate in isolation
   - Property test: across random equity/SL distances, position notional never
     exceeds 50% of equity
   - Scenario test: replay a synthetic equity curve with -3% intraday → verify
     daily loss limit fires once and resets at 00:00 UTC

DEFINITION OF DONE:
- All unit tests green.
- Live dry-run on testnet for 24h: sample 100+ approved/rejected decisions
  logged with reason; manual audit shows zero policy violations.
```

---

### PROMPT S8 — Execution Engine

```
Begin SECTION 8: Execution Engine.

CONTEXT: §6 in full (order types, slippage, retries, state machine, idempotency).

DELIVER (in TradingBot.Execution):

1. `OrderStateMachine` per §6.4 — complete with the transition table from the doc.

2. `ExecutionEngine : BackgroundService`:
   - Consumes approved Signal+Quantity intents from S7
   - Generates `clientOrderId = $"BOT-{strategy}-{signalId}-{guidShort}"` (≤36 chars)
   - Persists Orders row with Status=PENDING
   - Submits to Binance via S3 gateway with Polly resilience
   - Handles response: NEW → store ExchangeOrderId; REJECTED → log + RiskEvent
   - Reacts to userData WS executionReport / ORDER_TRADE_UPDATE events:
     * Insert Fills rows
     * Transition Order state (validated)
     * On terminal FILLED: open/extend Position row; submit bracket orders
     * On terminal CANCELED with partial fill: partial Position with reduced size

3. Bracket order placement:
   - Spot: native OCO via Binance.Net spot OCO endpoint
   - Futures: emulated bracket = STOP_MARKET (reduceOnly) + TAKE_PROFIT_MARKET (reduceOnly);
     when one fills, immediately cancel the other (cancelReplace pattern with
     reservation flag in DB to prevent races)

4. `TrailingStopManager : BackgroundService`:
   - Every bar close, for each open Position, compute new trail per §4.4
   - If new SL > current SL (long) or new SL < current (short), CANCEL existing
     STOP_MARKET and REPLACE with new one (idempotent via clientOrderId pattern)
   - Implements partial-take logic for trend strategy (50% at +2R)
   - Implements time-stop logic per strategy

5. `ReconciliationService : BackgroundService` (every 30s):
   - For every Order in non-terminal state older than 60s, GET /order from Binance
   - Apply state delta if exchange has progressed beyond local state
   - For every OPEN Position, compare DB qty vs Binance position; alert CRITICAL
     if drift > 0.5% — trip kill-switch if drift > $5

6. Slippage modeling for backtest hooks:
   - Implement the formula in §6.2; expose as `ISlippageModel`
   - Live engine uses zero-slippage assumption for our own books, but logs
     observed vs expected slippage per fill into a new table `ExecutionDiagnostics`
     (migration 007_exec_diag.sql)

7. Tests:
   - State machine table-driven tests covering all legal + illegal transitions
   - Mock-gateway integration tests for: full fill, partial fill, cancel race,
     network drop during submit (should not double-submit), HTTP 429, HTTP 418
   - Live testnet end-to-end: signal → order → fill → bracket → trailing update → exit

CONSTRAINTS:
- Idempotency: same SignalId NEVER produces two distinct exchange orders.
- Reconciliation must be safe to run concurrently with the engine.
- All exchange-mutating actions logged with CorrelationId = SignalId.

DEFINITION OF DONE:
- E2E testnet: a manually-injected signal flows through to filled position
  with bracket and trailing stop verified to update on bar close.
- Chaos test: kill the process during order submission; on restart,
  reconciliation either confirms order existed or creates a clean state.
```

---

### PROMPT S9 — AI Layer (Claude API)

```
Begin SECTION 9: AI Layer.

CONTEXT: §5 in full (use cases, prompts, caching, cost discipline).

DELIVER (in TradingBot.AI):

1. `IClaudeClient` wrapper around the Anthropic SDK:
   - SendAsync(string systemPrompt, string userPrompt, CacheControl, Schema?)
   - Returns AiResponse { Json, InputTokens, OutputTokens, LatencyMs, CostUsd }
   - Uses prompt caching headers for static system prompts

2. Cost & rate guard:
   - Token-bucket limiter (10 req/min default, configurable)
   - Daily $-cap from config; tripped → fall back to rule-only mode
   - Persist every call to dbo.AiInteractions
   - SHA-256(input) cache lookup before calling API; honor TTL per purpose
     (sentiment 5m, regime 1h, confirmation 30s, journal — no cache)

3. Four use cases, each with the EXACT prompts from §5.4:

   a. `INewsSentimentAnalyzer`:
      - Input: list of NewsItem (timestamp, source, headline)
      - Output: list of NdjsonSentiment{ asset, sentiment, confidence, horizon, rationale, actionable }
      - Persisted to new table `NewsSentiment` (migration 008_news.sql)
      - News source: pluggable via INewsSource (start with CryptoPanic free API + RSS fallback)
      - n8n workflow alternative: provide an HTTP webhook endpoint Newsfeed/Push
        so n8n can ship items to us; document both paths

   b. `IRegimeConfirmer`:
      - Called every 4h per active symbol after rule-based classifier
      - If Claude disagrees with confidence >0.7, persist BOTH and use Claude's
        verdict; otherwise keep rule-based
      - Logged to Regimes table with source='RULE' or 'CLAUDE_CONFIRMED'

   c. `ISetupConfirmer`:
      - Called only when rule-based composite confidence ∈ [0.5, 0.7]
      - 2-second timeout; on timeout, default to APPROVE with size_adj=0.7
      - Result feeds into final Signal.Confidence and Signal.Reason

   d. `IPostTradeJournalist`:
      - Sunday 06:00 UTC Hangfire job
      - Pulls last 7 days of TradeHistory + linked Signals
      - Calls Claude via Anthropic Message Batches API for 50% discount
      - Output: markdown report stored in `/journals/YYYY-WW.md` and emailed via n8n

4. Optional XGBoost local filter (skeleton only in this section):
   - IXgbSignalFilter interface
   - Training pipeline stub (we'll fill in Phase 2)
   - Inference stub returning 0.5 for now (no-op)

5. Tests:
   - Mock Claude client; verify prompt content matches §5.4 templates exactly
   - Cache hit test: same input → no second API call within TTL
   - Daily cap test: when cap exceeded, all callers get fallback decisions
   - Live integration test (gated by ANTHROPIC_API_KEY env): one real sentiment
     call on a sample headline, asserts schema-valid JSON response

CONSTRAINTS:
- Anthropic key from secrets. NEVER logged.
- Default model: claude-sonnet-4-5 (or current Sonnet); allow override per call.
- All calls async with CancellationToken.

DEFINITION OF DONE:
- Live test produces a valid sentiment JSON for a real headline.
- Cache prevents duplicate calls within TTL.
- Daily cap enforced (verified by setting cap to $0.01 and observing fallback).
- Sunday journal job runs and produces a markdown file.
```

---

### PROMPT S10 — Backtester + Walk-Forward + Monte Carlo

```
Begin SECTION 10: Backtester + Walk-Forward + Monte Carlo.

CONTEXT: §7 in full.

DELIVER (in TradingBot.Backtest):

1. `BacktestEngine` console host (separate executable):
   - Loads candles from SQL Candles table for a date range and symbol set
   - Replays bar-by-bar in OpenTime order
   - Reuses the SAME IIndicatorEngine, IStrategy, IRiskManager, BracketCalculator,
     OrderStateMachine code as live (zero drift)
   - Mocks IBinanceGateway with a `SimulatedExchange` that:
     * Fills LIMIT orders if the bar's range crosses the price
     * Fills MARKET orders at next bar's open + slippage from S8 model
     * Charges configurable fees (default: 0.02% maker / 0.05% taker UMfut;
       0.10% / 0.10% spot)
     * Handles STOP_MARKET / TAKE_PROFIT_MARKET on intra-bar high/low
     * Emits userData-shaped fill events to ExecutionEngine

2. Output:
   - `BacktestRun` row in new table `BacktestRuns` (migration 009_backtest.sql)
   - All simulated Signals/Orders/Fills/Positions/TradeHistory written to
     a parallel `_bt` schema or with a RunId column for separation
   - Equity curve CSV, drawdown curve CSV
   - Metrics report (markdown + JSON):
     * Sharpe, Sortino, Calmar
     * Deflated Sharpe Ratio (Bailey & López de Prado)
     * Max drawdown, recovery factor
     * Win rate, profit factor, avg trade duration
     * Per-strategy breakdown
     * Per-regime breakdown

3. `WalkForwardRunner`:
   - Configurable IS/OOS window sizes and step (default 6/1/1 months)
   - Parameter grid via JSON config
   - For each fold: optimize on IS → evaluate on OOS → record both metrics
   - Acceptance gate: OOS Sharpe ≥ 0.6× IS Sharpe in ≥70% of folds

4. `MonteCarloRunner`:
   - Trade-reshuffle: 1,000 simulations of random order; report 5/50/95 pct of MDD
   - Trade-skip stress: 10–15% random skips; report MDD inflation factor
   - Acceptance: 95th-pct MDD < 25%

5. CLI:
   - `dotnet run --project TradingBot.Backtest -- run --strategy TREND_EMA_ADX --symbol BTCUSDT --from 2024-01-01 --to 2026-01-01`
   - `dotnet run --project TradingBot.Backtest -- wfa --config wfa-config.json`
   - `dotnet run --project TradingBot.Backtest -- mc --runId <id>`

6. Tests:
   - Verify backtest of a constant-buy-and-hold matches manual calc
   - Verify replay determinism: same input → identical output
   - Verify lookahead-bias defense: strategies cannot see bars where OpenTime > replayClock

DEFINITION OF DONE:
- Run all 3 strategies on BTCUSDT 2024-01-01 → 2026-01-01 and produce reports.
- WFA produces a fold report with the 70%-acceptance verdict.
- MC produces 5/50/95 MDD.
- Determinism test passes.

Provide a SMOKE TEST CHECKLIST and an example command line.
```

---

### PROMPT S11 — Logging, Monitoring, Alerting

```
Begin SECTION 11: Logging, Monitoring, Alerting.

CONTEXT: §9.5, §9.6.

DELIVER:

1. Serilog finalization:
   - Enrichers: CorrelationId (SignalId-based AsyncLocal), MachineName, ThreadId
   - Sinks: Console, File (rolling daily, retain 30), Seq, Application Insights
     (Azure-only)
   - Levels per namespace from §9.5 example
   - Sensitive-data filter (mask api keys / order quantities optional)

2. Prometheus exporter (`prometheus-net.AspNetCore`) at `/metrics`:
   - All metrics from §9.5 (signals_total, orders_*, position_pnl_usd,
     account_equity_usd, drawdown_pct, ai_calls_total, ai_cost_usd_total,
     ws_reconnects_total, strategy_latency_ms histogram)

3. Grafana dashboard JSON:
   - Equity curve panel
   - Drawdown panel
   - Open positions table
   - WS health (last-message-timestamp)
   - Order fill latency P50/P95/P99
   - AI cost by purpose
   - Daily P&L bar chart
   Provide importable dashboard JSON.

4. Alerting:
   - `IAlertSink` with Telegram (via n8n webhook) and Email (SendGrid) implementations
   - Alert levels: INFO, WARN, ERROR, CRITICAL
   - Routing rules:
     * CRITICAL → Telegram + Email
     * ERROR    → Telegram
     * WARN     → digest every 6h
     * INFO     → log only
   - Deduplication: identical alerts within 5 min collapsed
   - Daily 06:00 UTC digest email summarizing P&L, trades, alerts

5. n8n workflow JSON exports:
   - workflow_telegram_alerts.json (Redis stream → Telegram bot)
   - workflow_news_ingest.json (CryptoPanic → POST to bot's news webhook)
   - workflow_daily_digest.json (HTTP GET bot summary → email)

6. Health checks:
   - /health/liveness  → process alive
   - /health/readiness → DB ping + Binance REST ping + WS connected + KillSwitch off

7. Tests:
   - Mock alert sinks; verify routing matrix and deduplication
   - Live test: trip a fake CRITICAL → assert Telegram message arrives

DEFINITION OF DONE:
- Grafana dashboard imports cleanly and shows live data within 1 minute.
- Telegram + email alerts arrive end-to-end on a test CRITICAL.
- Daily digest email arrives at 06:00 UTC the next morning.
```

---

### PROMPT S12 — Deployment + Go-Live

```
Begin SECTION 12: Deployment + Go-Live.

CONTEXT: §9 (deployment), §10 (roadmap exit criteria).

DELIVER:

1. Dockerfile (multi-stage, non-root, ~120MB final image).

2. docker-compose.yml stack:
   - tradingbot (worker)
   - sqlserver (or external Azure SQL)
   - seq
   - redis
   - n8n
   - prometheus + grafana
   - Persistent volumes for SQL data, Seq, Redis, Grafana

3. Cloud deployment (pick ONE based on my decision earlier):
   - Azure: Bicep templates for VM, Azure SQL, Key Vault, Log Analytics,
     Application Insights, NSG (egress-only to Binance + Anthropic + n8n cloud)
   - OR AWS: Terraform for EC2, RDS, Secrets Manager, CloudWatch
   Provide both as separate folders /infra/azure and /infra/aws and let me pick.

4. Quartz schedules wired:
   - Reference data refresh 00:05 UTC daily
   - Account snapshot every 1 min
   - Partition maintenance monthly day 1 01:00 UTC
   - Correlation matrix nightly 02:00 UTC
   - Gap detection every 5 min
   - Hangfire jobs:
     * Walk-forward weekly Sunday 02:00 UTC
     * Claude journal Sunday 06:00 UTC
     * XGBoost retrain Sunday 04:00 UTC (stub)

5. Disaster recovery scripts:
   - cold-start-reconciliation.ps1 (described in §9.7)
   - position-bootstrap.ps1
   - kill-switch-toggle.ps1 (manual)
   - paper-to-live-cutover.ps1

6. Go-live checklist (Markdown) covering:
   - Phase 3 exit criteria from §10
   - Pre-flight checks (API key permissions, IP whitelist, balances,
     reconciliation drift = 0, Telegram tested, kill-switch tested)
   - Capital scaling ladder ($2K → $10K → $20K → $50K with 4-week observation
     between rungs)
   - Rollback procedure (flip kill-switch, close positions reduceOnly, postmortem)

7. Runbook covering:
   - Daily ops: log review, alert review, daily P&L digest review
   - Weekly: Claude journal review, walk-forward refresh, correlation review
   - Monthly: partition maintenance verification, parameter review, capital review
   - Incident playbooks: API ban, exchange downtime, data drift, strategy degradation

8. Final smoke tests on production infra:
   - End-to-end testnet test in cloud env
   - Failover test: kill VM, verify auto-restart and reconciliation
   - Load test: simulate 100 signals/min, verify no DB lock contention

DEFINITION OF DONE:
- Full stack runs in cloud on testnet for 1 week unattended.
- All Phase 3 exit criteria documented and verified.
- Go-live checklist signed off.
- $2K live capital deployed; first 2 weeks of live data within expected envelope.
```

---

## 📋 HOW TO USE THIS DOCUMENT

1. **Open Claude Code in your IDE.**
2. **Paste PROMPT 0 + attach the design `.md`** → wait for the plan.
3. **Review the plan**, push back on anything you disagree with, get final sign-off.
4. **Paste PROMPT S1** → review code → run smoke tests → fix issues → commit.
5. Repeat for S2 → S12 in order. Do **NOT** skip ahead; later sections depend on earlier ones.
6. Between sections, run the smoke test checklist Claude Code provides before moving on.
7. Tag git: `phase1-mvp` after S6, `phase2-optimization` after S10, `phase3-production` after S12.

---

## ⚠️ CRITICAL RULES TO ENFORCE WITH CLAUDE CODE

Before each section, remind Claude Code of these:

- **Never log API keys or order quantities at INFO level.**
- **All money/price/qty as `decimal`, never `double`.**
- **All `DateTime` are UTC; ban `DateTime.Now`.**
- **Every exchange call wrapped in Polly + idempotent `clientOrderId`.**
- **No business logic in `Worker` host; it only wires DI and starts services.**
- **Tests required for every public method that has branching logic.**
- **No new NuGet package without justification + license check.**
- **Every section ends with a runnable smoke test.**
