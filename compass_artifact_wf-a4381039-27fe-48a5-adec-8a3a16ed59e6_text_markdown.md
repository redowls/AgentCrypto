# Binance AI-Assisted Automated Trading System — Senior Architect Technical Design Document

**Audience:** Senior quantitative trading architect / AI system designer
**Stack:** C# (.NET 8), SQL Server (SSMS), Binance.Net (JKorf), Skender.Stock.Indicators, Claude API, optional XGBoostSharp/ML.NET, n8n
**Account profile:** $10K–$50K, max drawdown 10–15%, max futures leverage 2–3×
**Timeframes:** 5m / 15m / 1h candles; intraday → multi-day holding

---

## 1. System Architecture

### 1.1 Component Inventory

| # | Component | Responsibility | Processing Mode |
|---|-----------|---------------|-----------------|
| 1 | **Market Data Ingestor** | REST snapshot + WebSocket kline/userData streams | Real-time event-driven |
| 2 | **Reference Data Service** | Symbols, filters (LOT_SIZE, PRICE_FILTER, MIN_NOTIONAL), funding schedules | Daily batch |
| 3 | **Candle Aggregator / Persistor** | Validates, deduplicates, gap-fills, persists OHLCV | Real-time, write-batched |
| 4 | **Indicator Engine** | ATR, ADX, EMA, RSI, BB, VWAP, Donchian (incremental & batch) | Real-time on bar close |
| 5 | **Signal Engine** | Rule-based strategy modules + ensemble selector | Event-driven on bar close |
| 6 | **AI Layer** | Claude sentiment/regime/confirmation; optional local XGBoost filter | Async (Claude), sync (ML) |
| 7 | **Risk Manager** | Position sizing, exposure caps, correlation, kill-switch | Synchronous gatekeeper |
| 8 | **Execution Engine** | Order placement, retries, OCO/TP-SL, state machine | Real-time |
| 9 | **Position & PnL Tracker** | Reconciles fills, tracks open exposure, mark-to-market | Real-time + 1-min snapshot |
| 10 | **Database Layer** | SQL Server (canonical store) + Redis (hot cache) | Mixed |
| 11 | **Backtester / Walk-Forward Runner** | Replays historical candles offline | Batch |
| 12 | **Monitoring & Alerting** | Serilog → Seq, Prometheus exporter, Telegram bot via n8n | Real-time |
| 13 | **n8n Orchestrator** | Cross-system glue: news ingestion, alerting, scheduled jobs | Event/timer driven |

### 1.2 Data Flow (Text Diagram)

```
                      ┌──────────────────────┐
                      │  Binance Spot/UM API │
                      │  (REST + WebSocket)  │
                      └──────────┬───────────┘
                                 │ klines, userData, depth
                                 ▼
   ┌──────────────────┐   ┌─────────────────────────┐
   │  REST Backfiller │──▶│  Market Data Ingestor   │
   │ (Quartz nightly) │   │  (Hosted BackgroundSvc) │
   └──────────────────┘   └────────────┬────────────┘
                                       │ Channel<Kline>
                                       ▼
                          ┌─────────────────────────┐
                          │  Candle Persistor +     │──┬──▶ SQL Server (Candles)
                          │  Indicator Engine       │  │
                          └────────────┬────────────┘  └──▶ Redis (latest bar/indicator cache)
                                       │ on bar-close event
                                       ▼
   ┌──────────────────┐    ┌─────────────────────────┐
   │  News Ingestor   │───▶│   AI Layer (Claude)     │
   │  (n8n workflow)  │    │  • sentiment            │◀──── Prompt cache (Redis)
   │  CryptoPanic etc │    │  • regime classify      │
   └──────────────────┘    │  • setup confirmation   │
                           └────────────┬────────────┘
                                        │ enriched signal
                                        ▼
                           ┌─────────────────────────┐
                           │  Signal Engine          │
                           │  • Breakout strategy    │
                           │  • MeanReversion        │  ──▶ SQL Server (Signals)
                           │  • TrendFollow          │
                           │  • Regime selector      │
                           └────────────┬────────────┘
                                        ▼
                           ┌─────────────────────────┐
                           │  Risk Manager           │
                           │  • Sizing (frac-Kelly)  │  ──▶ SQL Server (RiskEvents)
                           │  • Exposure caps        │
                           │  • Drawdown circuit-brk │
                           └────────────┬────────────┘
                                        ▼ approved order intent
                           ┌─────────────────────────┐
                           │  Execution Engine       │  ──▶ SQL Server (Orders, Fills)
                           │  • Order state machine  │
                           │  • Idempotency keys     │  ──▶ Binance REST (signed)
                           │  • Polly retries        │  ◀── userData WS (fills, ack)
                           └────────────┬────────────┘
                                        ▼
                           ┌─────────────────────────┐
                           │  Position/PnL Tracker   │  ──▶ SQL Server (Positions, AccountSnapshots)
                           └────────────┬────────────┘
                                        ▼
                           Serilog → Seq · Prometheus → Grafana · n8n → Telegram
```

### 1.3 Real-time vs Batch Decisions

| Component | Mode | Rationale |
|-----------|------|-----------|
| WS ingest, indicator update, signal eval | Real-time | Latency-sensitive; bar-close determinism |
| Candle persistence | Real-time, **write-batched** (50–500 row bulk insert per 1–2s) | SQL Server contention vs latency tradeoff |
| Reference data (symbols, filters) | Daily batch (Quartz cron 00:05 UTC) | Static-ish |
| Backtests / walk-forward | Batch (offline) | CPU-heavy, parameter sweeps |
| News sentiment refresh | Periodic batch (5–15 min) via n8n | Not latency-critical at 5m+ TF |
| Account snapshot | 1-minute timer + on-fill | Audit + drawdown tracking |

### 1.4 Microservices vs Monolith — Recommendation

**Recommended: Modular monolith (single .NET 8 Worker Service host with multiple `BackgroundService` modules), evolving toward 2–3 services in Phase 3.**

Justification:
- Account size $10K–$50K does not justify Kafka/Kubernetes operational overhead.
- All hot-path components (ingest → indicator → signal → risk → execution) sit in the same process; **in-process `System.Threading.Channels` keep end-to-end latency in the sub-millisecond range** for the trading hot path, which is exactly what RabbitMQ-style brokers add overhead to without benefit at this scale.
- One process = one consistent in-memory view of order state, dramatically simplifying the order state machine.
- A Phase 3 split-out is recommended only for: (a) the **Backtester** (separate console host so live trading is not impacted by replay CPU spikes), and (b) the **News/AI worker** (out-of-process so Claude API stalls cannot back-pressure the trading loop).

### 1.5 Message Queue / Event Bus

| Choice | Use it for | Don't use for |
|--------|-----------|---------------|
| **`System.Threading.Channels<T>`** (recommend) | Hot path: WS → aggregator → indicator → signal → risk → execution | Cross-process |
| **Redis Streams / Pub-Sub** | Cross-process events (news → trading bot, dashboard live view) | Durable audit (use SQL) |
| **RabbitMQ** | Only if you split into ≥3 services with at-least-once durability needs (e.g., paper vs live) | Hot path (adds 1–10ms broker latency per hop) |
| **Kafka** | **Not recommended** at this scale; Kafka shines >100K msg/s with replication ([Confluent benchmark](https://www.confluent.io/blog/kafka-fastest-messaging-system/)). For a single-account bot, it is over-engineering. |

**Default:** in-process channels for trading; Redis Streams between the bot and n8n; SQL Server is the audit-of-record.

---

## 2. Data Design (SQL Server)

### 2.1 Schema Principles

- **Canonical store** = SQL Server (durable, audit-grade). Redis is cache only.
- All times stored as `DATETIME2(3)` UTC. No local time anywhere.
- Money/price/qty stored as `DECIMAL(38,18)` to preserve crypto precision (Binance returns up to 8–18 decimals).
- Surrogate `BIGINT IDENTITY` PKs for entity tables; **composite natural keys** (Symbol, Interval, OpenTime) for time-series.
- Heavy fact tables (`Candles`) use **partition functions on month boundaries** + **clustered columnstore index** in Phase 3 for >100M rows ([Microsoft docs on columnstore](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/columnstore-indexes-overview)).

### 2.2 DDL — Reference & Time Series

```sql
-- =========================================================
-- Filegroups & Partition Function (run once during setup)
-- =========================================================
ALTER DATABASE TradingDb ADD FILEGROUP fg_candles_2026_q1;
ALTER DATABASE TradingDb ADD FILEGROUP fg_candles_2026_q2;
-- ...one per quarter

CREATE PARTITION FUNCTION pf_CandleMonth (DATETIME2(3))
    AS RANGE RIGHT FOR VALUES (
        '2026-01-01','2026-02-01','2026-03-01','2026-04-01',
        '2026-05-01','2026-06-01','2026-07-01','2026-08-01');

CREATE PARTITION SCHEME ps_CandleMonth
    AS PARTITION pf_CandleMonth ALL TO ([PRIMARY]);  -- start single-FG; split later

-- =========================================================
-- Symbols (reference)
-- =========================================================
CREATE TABLE dbo.Symbols (
    SymbolId        INT IDENTITY(1,1) PRIMARY KEY,
    Exchange        VARCHAR(16)  NOT NULL,            -- 'BINANCE_SPOT' | 'BINANCE_UMFUT'
    Symbol          VARCHAR(32)  NOT NULL,            -- 'BTCUSDT'
    BaseAsset       VARCHAR(16)  NOT NULL,
    QuoteAsset      VARCHAR(16)  NOT NULL,
    TickSize        DECIMAL(38,18) NOT NULL,
    StepSize        DECIMAL(38,18) NOT NULL,
    MinNotional     DECIMAL(38,18) NOT NULL,
    IsActive        BIT NOT NULL DEFAULT 1,
    UpdatedAt       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Symbols UNIQUE (Exchange, Symbol)
);

-- =========================================================
-- Candles (heavy time-series, partitioned)
-- =========================================================
CREATE TABLE dbo.Candles (
    SymbolId    INT          NOT NULL,
    Interval    VARCHAR(8)   NOT NULL,    -- '5m','15m','1h'
    OpenTime    DATETIME2(3) NOT NULL,
    CloseTime   DATETIME2(3) NOT NULL,
    [Open]      DECIMAL(38,18) NOT NULL,
    [High]      DECIMAL(38,18) NOT NULL,
    [Low]       DECIMAL(38,18) NOT NULL,
    [Close]     DECIMAL(38,18) NOT NULL,
    Volume      DECIMAL(38,18) NOT NULL,
    QuoteVolume DECIMAL(38,18) NOT NULL,
    TradeCount  INT          NOT NULL,
    TakerBuyBase DECIMAL(38,18) NOT NULL,
    IsClosed    BIT          NOT NULL,
    InsertedAt  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Candles PRIMARY KEY CLUSTERED
        (OpenTime, SymbolId, Interval)               -- partition key first
        ON ps_CandleMonth(OpenTime),
    CONSTRAINT FK_Candles_Symbols FOREIGN KEY (SymbolId)
        REFERENCES dbo.Symbols(SymbolId)
);

CREATE NONCLUSTERED INDEX IX_Candles_SymbolInterval_Time
    ON dbo.Candles (SymbolId, Interval, OpenTime DESC)
    INCLUDE ([Open],[High],[Low],[Close],Volume)
    ON ps_CandleMonth(OpenTime);
```

> **Phase-3 optimization:** add `CREATE CLUSTERED COLUMNSTORE INDEX CCI_Candles ON dbo.Candles_Archive` on a sibling archive table; switch out monthly partitions older than 6 months into the archive (~10× compression, ~10–100× scan speedup for backtest queries) ([Microsoft Learn](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/columnstore-indexes-overview)).

### 2.3 DDL — Trading Entities

```sql
CREATE TABLE dbo.Signals (
    SignalId        BIGINT IDENTITY(1,1) PRIMARY KEY,
    SymbolId        INT NOT NULL FOREIGN KEY REFERENCES dbo.Symbols(SymbolId),
    Strategy        VARCHAR(32) NOT NULL,           -- 'BREAKOUT_DON','MR_BB_VWAP','TREND_EMA_ADX'
    Interval        VARCHAR(8)  NOT NULL,
    BarOpenTime     DATETIME2(3) NOT NULL,
    Side            CHAR(4) NOT NULL,                -- 'BUY' | 'SELL'
    EntryPrice      DECIMAL(38,18) NOT NULL,
    StopLoss        DECIMAL(38,18) NOT NULL,
    TakeProfit      DECIMAL(38,18) NOT NULL,
    AtrValue        DECIMAL(38,18) NULL,
    Regime          VARCHAR(16) NULL,                -- TRENDING/RANGING/VOLATILE
    SentimentScore  DECIMAL(5,2) NULL,               -- -1.00 .. +1.00
    AiConfidence    DECIMAL(5,2) NULL,               -- 0..1 from Claude/XGB
    Confidence      DECIMAL(5,2) NOT NULL,           -- composite
    Status          VARCHAR(16) NOT NULL,            -- GENERATED|APPROVED|REJECTED|EXECUTED|EXPIRED
    Reason          NVARCHAR(512) NULL,              -- rejection reason / audit
    CreatedAt       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Signals_Sym_Time   ON dbo.Signals(SymbolId, BarOpenTime DESC);
CREATE INDEX IX_Signals_Status_CT  ON dbo.Signals(Status, CreatedAt DESC);

CREATE TABLE dbo.Orders (
    OrderId             BIGINT IDENTITY(1,1) PRIMARY KEY,
    SignalId            BIGINT NULL FOREIGN KEY REFERENCES dbo.Signals(SignalId),
    SymbolId            INT NOT NULL FOREIGN KEY REFERENCES dbo.Symbols(SymbolId),
    AccountType         VARCHAR(8) NOT NULL,         -- 'SPOT'|'UMFUT'
    ClientOrderId       VARCHAR(36) NOT NULL UNIQUE, -- idempotency key (newClientOrderId)
    ExchangeOrderId     BIGINT NULL,
    OrderType           VARCHAR(24) NOT NULL,        -- LIMIT|MARKET|STOP_MARKET|TAKE_PROFIT_MARKET|LIMIT_MAKER
    Side                CHAR(4) NOT NULL,
    PositionSide        VARCHAR(8) NULL,             -- futures hedge mode
    Quantity            DECIMAL(38,18) NOT NULL,
    Price               DECIMAL(38,18) NULL,
    StopPrice           DECIMAL(38,18) NULL,
    TimeInForce         VARCHAR(8) NULL,             -- GTC|IOC|FOK|GTX (post-only)
    ReduceOnly          BIT NOT NULL DEFAULT 0,
    Status              VARCHAR(24) NOT NULL,        -- see state machine §6.4
    FilledQty           DECIMAL(38,18) NOT NULL DEFAULT 0,
    AvgFillPrice        DECIMAL(38,18) NULL,
    CommissionPaid      DECIMAL(38,18) NOT NULL DEFAULT 0,
    CommissionAsset     VARCHAR(16) NULL,
    SubmittedAt         DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    LastUpdatedAt       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    Notes               NVARCHAR(512) NULL
);
CREATE INDEX IX_Orders_Symbol_Status ON dbo.Orders(SymbolId, Status);
CREATE INDEX IX_Orders_Submitted     ON dbo.Orders(SubmittedAt DESC);

CREATE TABLE dbo.Fills (
    FillId          BIGINT IDENTITY(1,1) PRIMARY KEY,
    OrderId         BIGINT NOT NULL FOREIGN KEY REFERENCES dbo.Orders(OrderId),
    TradeId         BIGINT NOT NULL,             -- exchange trade id
    Quantity        DECIMAL(38,18) NOT NULL,
    Price           DECIMAL(38,18) NOT NULL,
    Commission      DECIMAL(38,18) NOT NULL,
    CommissionAsset VARCHAR(16) NOT NULL,
    IsMaker         BIT NOT NULL,
    TradeTime       DATETIME2(3) NOT NULL,
    CONSTRAINT UQ_Fills UNIQUE (OrderId, TradeId)
);

CREATE TABLE dbo.Positions (
    PositionId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    SymbolId        INT NOT NULL FOREIGN KEY REFERENCES dbo.Symbols(SymbolId),
    AccountType     VARCHAR(8) NOT NULL,
    Side            CHAR(5) NOT NULL,            -- 'LONG' | 'SHORT'
    EntrySignalId   BIGINT NULL,
    EntryOrderId    BIGINT NULL,
    Quantity        DECIMAL(38,18) NOT NULL,
    AvgEntryPrice   DECIMAL(38,18) NOT NULL,
    StopLoss        DECIMAL(38,18) NOT NULL,
    TakeProfit      DECIMAL(38,18) NOT NULL,
    InitialRiskUsd  DECIMAL(38,8) NOT NULL,
    OpenedAt        DATETIME2(3) NOT NULL,
    ClosedAt        DATETIME2(3) NULL,
    ClosePrice      DECIMAL(38,18) NULL,
    RealizedPnlUsd  DECIMAL(38,8) NULL,
    Status          VARCHAR(16) NOT NULL,        -- OPEN|CLOSING|CLOSED
    CONSTRAINT FK_Pos_EntryOrder FOREIGN KEY (EntryOrderId) REFERENCES dbo.Orders(OrderId)
);
CREATE INDEX IX_Positions_Status ON dbo.Positions(Status, SymbolId);

CREATE TABLE dbo.TradeHistory (   -- denormalized closed-trade fact for analytics
    TradeHistoryId  BIGINT IDENTITY(1,1) PRIMARY KEY,
    PositionId      BIGINT NOT NULL,
    SymbolId        INT NOT NULL,
    Strategy        VARCHAR(32) NOT NULL,
    Side            CHAR(5) NOT NULL,
    EntryTime       DATETIME2(3) NOT NULL,
    ExitTime        DATETIME2(3) NOT NULL,
    HoldingMinutes  INT NOT NULL,
    EntryPrice      DECIMAL(38,18) NOT NULL,
    ExitPrice       DECIMAL(38,18) NOT NULL,
    Quantity        DECIMAL(38,18) NOT NULL,
    GrossPnlUsd     DECIMAL(38,8) NOT NULL,
    FeesUsd         DECIMAL(38,8) NOT NULL,
    NetPnlUsd       DECIMAL(38,8) NOT NULL,
    R_Multiple      DECIMAL(10,4) NOT NULL,        -- realized PnL / initial risk
    ExitReason      VARCHAR(24) NOT NULL           -- TP|SL|TRAIL|TIME|MANUAL|REGIME
);
CREATE INDEX IX_TH_Strategy_Exit ON dbo.TradeHistory(Strategy, ExitTime DESC);

CREATE TABLE dbo.AccountSnapshots (
    SnapshotId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    AccountType     VARCHAR(8) NOT NULL,
    SnapshotTime    DATETIME2(3) NOT NULL,
    EquityUsd       DECIMAL(38,8) NOT NULL,
    AvailableUsd    DECIMAL(38,8) NOT NULL,
    UnrealizedPnl   DECIMAL(38,8) NOT NULL,
    OpenPositions   INT NOT NULL,
    GrossExposure   DECIMAL(38,8) NOT NULL,        -- sum |notional|
    NetExposure     DECIMAL(38,8) NOT NULL,
    Drawdown        DECIMAL(7,4) NOT NULL          -- vs HWM
);
CREATE INDEX IX_Acct_Time ON dbo.AccountSnapshots(SnapshotTime DESC);

CREATE TABLE dbo.RiskEvents (
    RiskEventId     BIGINT IDENTITY(1,1) PRIMARY KEY,
    EventTime       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    EventType       VARCHAR(32) NOT NULL,          -- DAILY_LOSS_LIMIT|MAX_POS|CORR_BLOCK|CIRCUIT_BREAKER|API_BAN|...
    Severity        VARCHAR(8) NOT NULL,           -- INFO|WARN|ERROR|CRITICAL
    SymbolId        INT NULL,
    SignalId        BIGINT NULL,
    OrderId         BIGINT NULL,
    Payload         NVARCHAR(MAX) NULL,            -- JSON
    Acted           BIT NOT NULL DEFAULT 0
);
CREATE INDEX IX_Risk_Type_Time ON dbo.RiskEvents(EventType, EventTime DESC);

CREATE TABLE dbo.AiInteractions (
    AiInteractionId BIGINT IDENTITY(1,1) PRIMARY KEY,
    Purpose         VARCHAR(32) NOT NULL,          -- SENTIMENT|REGIME|CONFIRM|JOURNAL
    Model           VARCHAR(48) NOT NULL,
    InputHash       CHAR(64) NOT NULL,             -- SHA-256 for cache lookup
    InputJson       NVARCHAR(MAX) NOT NULL,
    OutputJson      NVARCHAR(MAX) NULL,
    InputTokens     INT NULL,
    OutputTokens    INT NULL,
    LatencyMs       INT NULL,
    CostUsd         DECIMAL(10,6) NULL,
    CreatedAt       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Ai_Hash ON dbo.AiInteractions(InputHash);
```

### 2.4 Indexing Strategy Summary

| Table | Clustered | Key Non-Clustered |
|-------|-----------|-------------------|
| `Candles` | `(OpenTime, SymbolId, Interval)` partitioned | `(SymbolId, Interval, OpenTime DESC) INCLUDE OHLCV` |
| `Signals` | `SignalId` | `(SymbolId, BarOpenTime)`, `(Status, CreatedAt)` |
| `Orders` | `OrderId` | `(ClientOrderId UNIQUE)`, `(SymbolId, Status)` |
| `Fills` | `FillId` | `(OrderId, TradeId UNIQUE)` |
| `Positions` | `PositionId` | `(Status, SymbolId)` |
| `TradeHistory` | `TradeHistoryId` | `(Strategy, ExitTime DESC)` |

**Rule of thumb:** clustered index on partition key first for `Candles` so partition-elimination works on date-range queries; the most-recent partition stays hot in buffer pool. Always include the partition column (`OpenTime`) in `WHERE` clauses ([SQL Server partitioning best practices](https://www.cathrinewilhelmsen.net/table-partitioning-in-sql-server/)).

### 2.5 Partitioning & Retention Policy

| Data | Retention | Storage Tier |
|------|-----------|--------------|
| 1m candles (if collected) | 90 days hot, then aggregated to 5m | rowstore → archive |
| 5m / 15m / 1h candles | 5 years | hot 6 mo, columnstore archive thereafter |
| Signals, Orders, Fills | 7 years (audit) | rowstore |
| AiInteractions | 1 year (cost analysis) | rowstore + monthly export to blob |
| AccountSnapshots | indefinite | rowstore (low volume) |

**Sliding window automation:** monthly Quartz job `PartitionMaintenance` that (a) `SPLIT RANGE` to add next month, (b) `SWITCH PARTITION` on the 6-month-old partition into a staging table, (c) inserts into archive, (d) `MERGE RANGE` to drop the old boundary. This is the canonical sliding-window pattern ([SQL Server partitioning](https://www.cathrinewilhelmsen.net/series/table-partitioning-in-sql-server/)).

---

## 3. Trading Strategy Design

### 3.1 Strategy A — Donchian Breakout with Volume Confirmation (BREAKOUT_DON)

**Hypothesis:** Crypto exhibits strong continuation after volatility expansion through a recent extreme; momentum-trend strategies have shown statistically significant predictability in the crypto literature ([Bouri et al. surveyed in ResearchGate](https://www.researchgate.net/figure/This-table-reports-the-annualized-return-Sharpe-Sortino-and-Calmar-ratios-for-the_tbl6_335519898)).

**Indicators / parameters (1h primary, 15m for entry timing):**
| Parameter | Value |
|-----------|-------|
| Donchian length | 20 (1h) |
| Volume MA length | 20 |
| Volume confirmation multiplier | ≥ 1.5× SMA20(volume) |
| Trend filter | Close > EMA(200) on 1h for longs, < for shorts |
| ATR length | 14 |
| ADX length / threshold | 14 / ADX > 20 (avoid pure-chop ranges) |

**Entry (long):**
```
let upperBand = Highest(High, 20);
buySignal =
       (Close > upperBand[1])             -- breakout of prior 20-bar high
   AND (Volume >= 1.5 * SMA(Volume,20))   -- volume confirmation
   AND (Close > EMA(Close,200))           -- macro-trend filter
   AND (ADX(14) > 20);                    -- regime is trending/expanding
-- short = mirror with lowerBand
```

**Exit:**
- Initial stop: `entry - 1.5 * ATR(14)`
- Initial TP: `entry + 3.0 * ATR(14)` (1:2 RR)
- After +1.5 ATR favorable, switch to **Chandelier exit**: `Highest(High, 22) - 3 * ATR(22)` ([Skender library implements ChandelierExit natively](https://daveskender.github.io/Stock.Indicators/))
- Time stop: close at market if not at +1R within 24 bars (24h on 1h TF)

**Suitable regime:** TRENDING or VOLATILE-EXPANSION. Disable in RANGING.
**Expected profile:** win rate 35–45%, profit factor 1.4–1.8, RR 1:2 — typical breakout statistics ([Donchian breakout characteristics](https://www.luxalgo.com/blog/donchian-channels-breakout-and-trend-following-strategy/)).

### 3.2 Strategy B — Mean Reversion with RSI + Bollinger + VWAP (MR_BB_VWAP)

**Hypothesis:** Short-horizon overshoots in liquid crypto pairs revert to the session VWAP / 20-period mean; multi-indicator confirmation (RSI extreme + BB tag + VWAP side) reduces false signals ([VWAP-enhanced BB MR strategy](https://medium.com/@FMZQuant/vwap-enhanced-bollinger-bands-momentum-reversal-strategy-570b86982021)).

**Indicators (15m primary):**
| Parameter | Value |
|-----------|-------|
| RSI length | 14, oversold 25, overbought 75 |
| Bollinger | 20-period, 2.0 σ |
| VWAP | session-anchored (UTC daily reset) |
| ATR length | 14 |
| ADX filter | ADX(14) **<** 25 (only enter in ranges) |

**Entry (long):**
```
buySignal =
       (Close < LowerBB)         -- price stretched below band
   AND (RSI(14) < 25)            -- momentum oversold
   AND (Close > VWAP * 0.985)    -- still within "value zone" of VWAP (avoid catching falling knife)
   AND (ADX(14) < 25)            -- regime is ranging
   AND (Close > Close[3]);       -- micro-confirmation: 3-bar reversal beginning
```

**Exit:**
- Stop: `entry - 1.0 * ATR(14)` (tighter than breakout because mean-reversion edge is in fast snap-backs)
- TP1 at VWAP / middle BB → take 50% off
- TP2 at upper BB or +1.5R → close remainder
- Time stop: 8 bars (2h on 15m TF) — research shows MR edge decays quickly

**Suitable regime:** RANGING / low-volatility. Hard-disable when ADX > 30 or news event flagged by Claude.
**Expected profile:** win rate 55–65%, RR 1:1.2–1:1.5, profit factor 1.3–1.6.

### 3.3 Strategy C — EMA Crossover with ADX Filter (TREND_EMA_ADX)

**Hypothesis:** Trend-following with regime confirmation. Raw 9/21 EMA crossovers produce ~35–40% win rate and break-even PF; adding **higher-timeframe trend alignment** + **ADX > 25** lifts win rate to 50–60% ([CrossTrade](https://crosstrade.io/learn/trading-strategies/moving-average-crossover); [QuantStock](https://quantstock.org/blog/ema-vs-sma-crossover-strategy)).

**Indicators (1h):**
| Parameter | Value |
|-----------|-------|
| Fast EMA | 9 |
| Slow EMA | 21 |
| Trend EMA | 50 (same TF) and 200 (4h, HTF filter) |
| ADX length / threshold | 14 / > 25 |
| ATR length | 14 |

**Entry (long):**
```
buySignal =
       Crossover(EMA9, EMA21)
   AND (Close > EMA50)
   AND (Close_4h > EMA200_4h)              -- HTF alignment
   AND (ADX(14) > 25)
   AND (NOT IsExplosiveBar);                -- skip if range > 2.5*ATR(20) to avoid exhaustion entries
```

**Exit:**
- Stop: `entry - 2.0 * ATR(14)` (wider — riding trends needs slack)
- Trail: ATR-trail at `2 * ATR` once +1R reached; alternatively EMA21 trail
- TP target: optional partial at +2R (50%), let runner ride trail
- Hard time stop: 5 days (120 bars on 1h)

**Suitable regime:** TRENDING. Disable in RANGING/COMPRESSING.
**Expected profile:** win rate 40–50%, RR 1:2.5–1:3, profit factor 1.5–2.0 (asymmetric — large winners cover frequent small losers).

### 3.4 Regime-Based Strategy Selector

The Signal Engine queries the regime classifier (rule-based + Claude check) every bar close and routes:

| Regime | Active strategies | Rationale |
|--------|-------------------|-----------|
| TRENDING (ADX>25, BBW expanding) | TREND_EMA_ADX (primary), BREAKOUT_DON (secondary) | Both edge-positive in trends |
| RANGING (ADX<20, BBW < 0.7×SMA(BBW,50)) | MR_BB_VWAP only | Trend strategies whipsaw |
| VOLATILE (ATR > 1.5× ATR-50 SMA, ADX 20–25) | BREAKOUT_DON only, **half size** | Breakouts continue but fakeouts costly |
| COMPRESSING (BBW low, ADX rising) | None — wait for resolution | Don't pre-position |

Regime classifier follows the multi-factor approach in the literature ([LuxAlgo](https://www.luxalgo.com/blog/market-regimes-explained-build-winning-trading-strategies/), [Thrive](https://thrive.fi/blog/trading/crypto-market-regime-detection)): rule-based primary classifier (ADX + BBW percentile + ATR ratio) confirmed by Claude every 4 hours.

---

## 4. Stop Loss & Take Profit Model

### 4.1 Fixed vs Dynamic — Comparison

| | Fixed % SL/TP | ATR-based dynamic |
|---|---|---|
| Adapts to volatility regime | ❌ | ✅ |
| Stops too tight in volatile periods | Often | Rare |
| Stops too wide in calm periods | Rare | Adapts |
| Easy to communicate | ✅ | ⚠ requires ATR calc |
| Recommended | Only for paper-trade hello-world | **Production default** |

**Decision:** dynamic ATR-based stops everywhere ([standard practice in quant trading literature](https://medium.com/superalgos/basics-of-risk-management-at-trading-stop-loss-take-profit-and-position-sizing-with-atr-cafe35dec774); [Forex Tester](https://forextester.com/blog/stop-loss-and-take-profit/)).

### 4.2 ATR-Based Stop & Target Formula

```csharp
public static (decimal sl, decimal tp) ComputeBracket(
    decimal entry, decimal atr14, OrderSide side, StrategyType s)
{
    var (slMult, tpMult) = s switch
    {
        StrategyType.Breakout      => (1.5m, 3.0m),  // 1:2
        StrategyType.MeanReversion => (1.0m, 1.5m),  // 1:1.5
        StrategyType.Trend         => (2.0m, 5.0m),  // 1:2.5 (with trail)
        _ => (1.5m, 3.0m)
    };
    return side == OrderSide.Buy
        ? (entry - slMult * atr14, entry + tpMult * atr14)
        : (entry + slMult * atr14, entry - tpMult * atr14);
}
```

**Volatility-adjusted TP refinement:** scale TP by current ATR vs its 50-bar SMA. If `atrRatio = atr14 / sma(atr14,50) > 1.3`, reduce TP multiplier 20% (don't be greedy in bursting volatility); if `< 0.7`, extend TP 20%.

### 4.3 Risk-Reward Targets per Strategy

| Strategy | SL (×ATR) | TP (×ATR) | RR | Trail |
|----------|----------|----------|----|----|
| Breakout (BREAKOUT_DON) | 1.5 | 3.0 | 1:2 | Chandelier 22, 3×ATR after +1.5R |
| Mean Reversion (MR_BB_VWAP) | 1.0 | 1.5 | 1:1.5 | None (fast in/out) |
| Trend (TREND_EMA_ADX) | 2.0 | 5.0* | 1:2.5 | EMA21 or 2×ATR Chandelier |

*= partial 50% at +2R, runner trails.

### 4.4 Trailing Stop Logic (C# Pseudocode)

```csharp
public decimal UpdateTrail(Position p, Candle bar, decimal atr)
{
    decimal newSL = p.StopLoss;
    if (p.Side == Side.Long)
    {
        // Activate trail only after 1.5R favorable
        if (bar.Close - p.AvgEntryPrice >= 1.5m * p.InitialRiskPerUnit)
        {
            decimal chandelier = p.HighestHighSinceEntry - 3m * atr;
            decimal emaTrail   = bar.Ema21;        // strategy-dependent
            decimal candidate  = Math.Max(chandelier, emaTrail);
            newSL = Math.Max(p.StopLoss, candidate); // monotonic upward only
        }
    }
    else { /* mirror for short */ }
    return newSL;
}
```

### 4.5 Time-Based Exits

| Strategy | Time stop |
|----------|-----------|
| MR_BB_VWAP | 8 × 15m = 2h |
| BREAKOUT_DON | 24 × 1h = 24h if not at +1R |
| TREND_EMA_ADX | 5 days (120 × 1h) hard cap |

Also: a global **funding-aware exit** for futures — if the next 8h funding payment would exceed 0.05% (annualized ~55%) and the position is on the paying side and in profit < 0.3 ATR, close before funding ([Binance funding](https://www.binance.com/en/blog/futures/what-is-futures-funding-rate-and-why-it-matters-421499824684903247)).

---

## 5. AI / Analysis Layer

### 5.1 Where Claude Adds Value (and Where It Doesn't)

| Use case | Claude? | Latency tolerance | Why |
|----------|---------|-------------------|-----|
| Crypto news headline → bullish/bearish score | ✅ | seconds | LLM excels at unstructured text classification with reasoning |
| Market regime confirmation (every 4h) | ✅ | seconds | Synthesize multi-indicator state in plain language; sanity-check rule-based classifier |
| Pre-execution setup confirmation (if confidence borderline) | ✅ (gated) | <2s budget | Catches obvious red flags rule logic misses |
| Post-trade journaling / weekly review | ✅ (batch) | minutes | Not in critical path |
| Tick-by-tick decisions, indicator math, SL/TP computation | ❌ | ms | Way too slow & expensive; deterministic code wins |
| High-frequency signal filtering at scale | ❌ → use **XGBoost/LightGBM** locally via XGBoostSharp | <10ms | Local ML inference is ~1000× faster and free per call |

### 5.2 Claude API — Cost & Rate Discipline

Per Anthropic's published pricing for Sonnet 4.5: **$3/MTok input, $15/MTok output**, with prompt caching at **10% of base input price for cache reads** (5-min default TTL) and a 1.25× multiplier on cache writes ([Claude pricing docs](https://platform.claude.com/docs/en/about-claude/pricing); [Finout pricing summary](https://www.finout.io/blog/anthropic-api-pricing)). Batch API offers **50% discount** for non-time-critical workloads.

**Cost budget (target $30–60 / month for $20K account):**

| Call type | Frequency | Avg in-tokens | Avg out-tokens | Cost/call | Daily cost (5 symbols) |
|-----------|-----------|---------------|----------------|-----------|------------------------|
| Sentiment (per news batch, 5 min) | 288/day | 1,500 (cached system) | 200 | ~$0.005 | ~$1.5 |
| Regime check (per symbol, 4h) | 30/day | 800 | 150 | ~$0.005 | ~$0.15 |
| Setup confirmation (only borderline) | ~10/day | 1,200 | 300 | ~$0.008 | ~$0.08 |
| Weekly journal | 1/week (batch) | 30K | 3K | ~$0.07 | trivial |

**Caching:** put the static system prompt + symbol/strategy reference in the cached prefix; Claude's prompt cache only charges 10% on hits ([Rate limits docs](https://platform.claude.com/docs/en/api/rate-limits)).
**Rate limiting:** wrap all Claude calls in a token-bucket limiter (e.g., 10 RPM); if budget exceeded, **fall back to rule-based-only** (degraded but functional). Persist every call to `dbo.AiInteractions` with input hash for dedup/cache.

### 5.3 Where Local ML Makes Sense

A second-stage **XGBoost binary classifier** (signal → "trade" / "skip") trained on the last 12 months of generated signals + outcomes (R-multiple as the label thresholded at 0). Features = the 20–30 indicator values at signal time + regime + sentiment. Train weekly (Hangfire job), persist as JSON model, score in <1ms in-process via `XGBoostSharp` ([XGBoostSharp on GitHub](https://github.com/mdabros/XGBoostSharp)) or a `SharpLearning` model. Use for **confidence scoring**, not as a binary block — cap influence to ±20% of position size, never reverse direction.

### 5.4 Concrete Prompt Templates

**5.4.1 News sentiment (cached system prompt):**
```
SYSTEM (cached):
You are a crypto news sentiment classifier for an algorithmic trading bot.
You read short crypto news headlines and produce a structured JSON verdict.
Sentiment scale: -1.0 (very bearish) ... +1.0 (very bullish), 0 = neutral.
Confidence scale: 0.0 ... 1.0.
You MUST output ONLY a JSON object. No prose, no markdown.

Schema:
{
  "asset": "<ticker or GLOBAL>",
  "sentiment": <number -1..+1>,
  "confidence": <number 0..1>,
  "horizon": "INTRADAY|SWING|LONG",
  "rationale": "<<= 25 words>",
  "actionable": true|false
}

USER:
<news_items>
  <item ts="2026-04-25T08:12Z" source="CoinDesk">
    SEC delays decision on spot ETH ETF amendment to June 5
  </item>
  <item ...>
</news_items>

For each item, return one JSON object on its own line (NDJSON).
```

**5.4.2 Regime classification (called every 4h per symbol):**
```
SYSTEM (cached):
You are a market regime classifier. Given current technical readings, classify the
regime as exactly one of: TRENDING_UP, TRENDING_DOWN, RANGING, VOLATILE, COMPRESSING.
Output strict JSON with regime, confidence (0..1), and a 1-line reason.

USER:
Symbol: BTCUSDT  TF: 1h
Readings:
  ADX(14)=28.4
  +DI=24.1  -DI=15.7
  ATR(14)=412.5  ATR50_SMA=380.2  ATR_ratio=1.085
  BBWidth_pct=0.038  BBWidth_pct_50pctl=0.42
  EMA(9)=64210  EMA(21)=63880  EMA(50)=63110  EMA(200)=58740
  Last 20 closes slope/bar: +0.18%
Output JSON.
```

**5.4.3 Setup confirmation (called only when rule-confidence in [0.5, 0.7]):**
```
SYSTEM (cached):
You are a senior trade reviewer for a quant crypto bot.
Given a proposed trade and supporting context, output a JSON verdict:
{ "approve": true|false, "confidence": 0..1, "concerns": ["..."], "size_adj": 0.5..1.0 }
Reject only on clear red flags: major news contradiction, regime mismatch, late-cycle entry.

USER:
Strategy: BREAKOUT_DON  Symbol: SOLUSDT  Side: BUY
Entry: 178.42  SL: 174.10 (1.5*ATR)  TP: 187.06 (3*ATR)
Regime (rule): TRENDING_UP (ADX 31)
News sentiment last 6h: +0.42 (3 items)
Setup features:
  Breakout magnitude: +0.38% above prior 20-bar high
  Volume confirmation: 1.7x SMA20
  EMA200 distance: +6.4%
  Last 5 BREAKOUT_DON trades on this symbol: 3W/2L, avg R = +0.42
Concerns to consider: late entry, exhaustion, news risk in next 8h.
```

**5.4.4 Post-trade journaling (Sunday batch):**
```
SYSTEM (cached):
You are an objective post-trade analyst. Given a CSV of last week's closed trades with
context, produce: (a) top 3 patterns of winners, (b) top 3 patterns of losers,
(c) one concrete parameter or rule change to test next week (with hypothesis).
Markdown output.

USER:
<csv of last week's trades from dbo.TradeHistory + linked Signals>
```

### 5.5 Caching, Rate Limiting & Cost Management

- **Hash-based cache:** SHA-256 of `(purpose, model, normalized_input)` → look up `AiInteractions` first; reuse if < TTL (sentiment 5 min, regime 1h, confirmation 30s).
- **Prompt caching:** static system prompts served from Anthropic's cache; cache hit = 10% of base price ([Anthropic docs](https://platform.claude.com/docs/en/about-claude/pricing)).
- **Token bucket:** `SemaphoreSlim`-backed limiter — 10 calls/min, daily $-cap.
- **Circuit breaker:** if 3 consecutive 5xx/timeouts from Claude in 60s, open the breaker for 5 min, fall back to rule-only signals; raise a `RiskEvents` row of severity WARN.
- **Batch journal calls** through Anthropic's Message Batches API (50% discount, ≤24h SLA — fine for weekly journals).

---

## 6. Execution Engine

### 6.1 Order Type Selection — Tradeoffs

| Order type | Pros | Cons | When |
|------------|------|------|------|
| MARKET | Guaranteed fill | Slippage, taker fee 0.05% futures / 0.10% spot | Stop-out, must-exit |
| LIMIT (GTC) | Price control, maker fee 0.02% futures / 0.10% spot | May not fill | Calm conditions, planned entries |
| LIMIT_MAKER (POST_ONLY / GTX) | Guaranteed maker fee, no taker risk | Rejected if would cross | Low-urgency entries when patient |
| STOP_MARKET / TAKE_PROFIT_MARKET | Triggered exits | Slippage at trigger | All SL exits in fast moves |
| OCO (spot) | Atomic SL+TP pair | Spot-only; futures has no native OCO ([dev.binance.vision](https://dev.binance.vision/t/binance-futures-oco-order-or-sl-tp/5460)) | Spot bracket orders |

**Defaults:**
- Entry: LIMIT with `IOC` 5 ticks through mid (fills immediately if liquidity, else cancels and retries with MARKET if breakout still valid). For mean-reversion: LIMIT_MAKER at signal price ± 1 tick (collect maker rebate).
- Stop-loss: STOP_MARKET (we want guaranteed exit on adverse move; slippage is the lesser evil vs unfilled stop-limit).
- Take-profit: TAKE_PROFIT_MARKET for primary; secondary partial via LIMIT (more passive).
- Spot brackets: place OCO immediately after entry fill ([Binance OCO](https://academy.binance.com/en/articles/what-is-an-oco-order)).
- Futures brackets: emulate OCO via two **`reduceOnly`** orders (STOP_MARKET + TAKE_PROFIT_MARKET) with mutual cancel logic in our state machine — Binance futures does **not** offer native OCO.

### 6.2 Slippage Modeling

Empirical research suggests **square-root impact** under-estimates real crypto slippage in low-participation regimes; sigmoid-adjusted is closer to reality ([Talos market-impact model](https://www.talos.com/insights/understanding-market-impact-in-crypto-trading-the-talos-model-for-estimating-execution-costs)).

Practical model used in backtesting and pre-trade checks:
```
expected_slippage_bps =
    spread_bps * 0.5                                        // half-spread
  + impact_coef * sqrt(orderNotional / 1mDollarVolume) * 100 // sqrt impact
  + atrAdjustment                                            // +20% in high ATR
```

For our $10K–50K accounts with single positions ≤$1,000–5,000 in BTC/ETH/SOL on Binance, real-world slippage is typically **1–3 bps for limit orders, 5–10 bps for taker market orders on majors**. Backtests must charge at least the higher value; small altcoins should be excluded from production until depth profile is measured.

### 6.3 Retry, Backoff & Failure Handling (Polly-based)

```csharp
// Polly v8 resilience pipeline for Binance trading calls
var pipeline = new ResiliencePipelineBuilder<RestCallResult<BinanceOrder>>()
  .AddRetry(new RetryStrategyOptions<RestCallResult<BinanceOrder>>
  {
      ShouldHandle = new PredicateBuilder<RestCallResult<BinanceOrder>>()
          .Handle<HttpRequestException>()
          .Handle<TimeoutRejectedException>()
          .HandleResult(r => r.Error?.Code is -1001 or -1003 or -1015 or -1021 // network/recvWindow/rate
                           || (r.ResponseStatusCode >= 500 && r.ResponseStatusCode < 600)),
      MaxRetryAttempts = 4,
      BackoffType = DelayBackoffType.Exponential,
      UseJitter = true,                                  // decorrelated jitter (Polly recommended)
      Delay = TimeSpan.FromMilliseconds(250),
      OnRetry = args => { _logger.LogWarning("Retry {Attempt} after {Delay}",
                              args.AttemptNumber, args.RetryDelay); return ValueTask.CompletedTask; }
  })
  .AddTimeout(TimeSpan.FromSeconds(8))
  .AddCircuitBreaker(new CircuitBreakerStrategyOptions<RestCallResult<BinanceOrder>>
  {
      FailureRatio = 0.5, MinimumThroughput = 8, SamplingDuration = TimeSpan.FromSeconds(30),
      BreakDuration = TimeSpan.FromMinutes(1)
  })
  .Build();
```

Specific cases:
- **HTTP 429** (rate limit) + Binance `retryAfter` field → respect it, suspend submitter that long, halve order rate budget for 5 minutes.
- **HTTP 418** (IP-banned) → immediate kill-switch; no retries; alert CRITICAL via Telegram.
- **Filter rejects** (LOT_SIZE / MIN_NOTIONAL / PRICE_FILTER) → re-clamp using `BinanceHelpers.ClampQuantity` from Binance.Net before retry; if still invalid, log to RiskEvents and skip.
- **Partial fill + cancel races** → the FIX-style state matrix from FIX Trading Community is canonical: states `Filled` and `Canceled` are terminal ([FIX state matrix](https://www.fixtrading.org/online-specification/order-state-changes/)); after `PARTIALLY_FILLED → CANCELED`, the position size = filled-qty; we do not "complete" the planned size unless re-entry signal still valid.
- **Network errors with no response** → query `GET /api/v3/order` by `clientOrderId` to discover whether the order actually landed before retrying (avoids accidental duplicates).

### 6.4 Order State Machine

States (canonical, matches Binance + FIX):

```
            ┌─────────────┐
            │  PENDING    │ (we created locally, not yet submitted)
            └──────┬──────┘
                   │ submit (with newClientOrderId = idempotency key)
                   ▼
            ┌─────────────┐
            │     NEW     │◀─────────── from exchange ack
            └──┬──────┬───┘
               │      │
   partial fill│      │ full fill
               ▼      ▼
        ┌─────────────────┐
        │ PARTIALLY_FILLED│
        └──────┬──────────┘
               │ remaining filled / cancel-rest
               ▼          ▼
            ┌──────┐   ┌────────┐   ┌───────────┐  ┌──────────┐
            │FILLED│   │CANCELED│   │  REJECTED │  │ EXPIRED  │
            └──────┘   └────────┘   └───────────┘  └──────────┘
                       (terminal)
```

Idempotency: every order carries `newClientOrderId = $"BOT-{strategy}-{signalId}-{guidShort}"` (≤36 chars). Binance accepts a new order with the same `clientOrderId` only after the previous one is filled/expired ([Binance docs](https://developers.binance.com/docs/binance-spot-api-docs/websocket-api/trading-requests)). Local DB constraint `UNIQUE(ClientOrderId)` enforces one row per logical attempt.

```csharp
public sealed class OrderStateMachine
{
    private static readonly Dictionary<OrderState, HashSet<OrderState>> Allowed =
        new()
        {
            [OrderState.Pending]          = new() { OrderState.New, OrderState.Rejected },
            [OrderState.New]              = new() { OrderState.PartiallyFilled, OrderState.Filled,
                                                    OrderState.Canceled, OrderState.Expired,
                                                    OrderState.Rejected },
            [OrderState.PartiallyFilled]  = new() { OrderState.Filled, OrderState.Canceled,
                                                    OrderState.Expired },
            // terminal:
            [OrderState.Filled] = [], [OrderState.Canceled] = [],
            [OrderState.Rejected] = [], [OrderState.Expired] = []
        };

    public bool TryTransition(Order o, OrderState next, out string? error)
    {
        if (!Allowed[o.Status].Contains(next))
        { error = $"Illegal transition {o.Status} -> {next}"; return false; }
        o.Status = next; o.LastUpdatedAt = DateTime.UtcNow; error = null; return true;
    }
}
```

State transitions are driven primarily by the **userData WebSocket** (`executionReport` for spot, `ORDER_TRADE_UPDATE` for futures). REST `GET /order` is the reconciliation fallback called every 30s for any non-terminal order older than 60s.

---

## 7. Backtesting & Validation

### 7.1 Framework — Build vs Buy

There is no dominant C# crypto-backtesting library equivalent to Python's vectorbt/backtrader. **Recommendation: build a thin custom engine** that:
- Reuses the **same** Indicator Engine and Signal/Risk modules as live (zero code drift between sim and prod).
- Replays Candles from SQL Server in `OpenTime` order, calling strategy code with the same `IOnBarClose` interface as live.
- Models fees (configurable maker/taker), latency, and slippage explicitly.

Skender.Stock.Indicators provides 90+ indicators including ATR, ADX, EMA, RSI, BB, Donchian, ChandelierExit out of the box ([Skender on NuGet](https://www.nuget.org/packages/Skender.Stock.Indicators/), [docs](https://daveskender.github.io/Stock.Indicators/)) — use it directly.

### 7.2 Walk-Forward Methodology

Rolling-window WFA per Pardo's framework ([Surmount overview](https://surmount.ai/blogs/walk-forward-analysis-vs-backtesting-pros-cons-best-practices); [PyQuant News](https://www.pyquantnews.com/free-python-resources/the-future-of-backtesting-a-deep-dive-into-walk-forward-analysis)):

| | Setting |
|---|---|
| In-sample window | 6 months |
| Out-of-sample window | 1 month |
| Step (anchored = false) | 1 month |
| Total span | 24+ months |
| Parameters to optimize | ATR multiplier (SL/TP), Donchian length, ADX threshold |
| Optimization metric | Deflated Sharpe Ratio (avoid selection bias) |
| Acceptance | OOS Sharpe ≥ 0.6 × IS Sharpe in ≥70% of folds |

### 7.3 Key Metrics

| Metric | Target | Notes |
|--------|--------|-------|
| Sharpe ratio (annualized) | ≥ 1.0; "respectable" 0.8–1.2 in crypto ([BingX](https://bingx.com/en/learn/article/what-is-sharpe-ratio-in-crypto-trading-how-to-use)) | Penalizes upside vol — flag if Sortino >> Sharpe |
| Sortino ratio | ≥ 1.5 | Downside-only |
| Calmar ratio | ≥ 1.0 (return/MDD) | |
| **Deflated Sharpe** ([Bailey & López de Prado 2014](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2460551)) | p-value < 0.05 after multiple-testing correction | THE robustness gate |
| Max drawdown | < 15% | Hard account-level constraint |
| Win rate | varies by strategy (35–65%) | |
| Profit factor | ≥ 1.4 | |
| Avg trade duration | match design (MR <2h, BO <24h, TR <5d) | |
| Trade count (sample size) | ≥ 100 in-sample, ≥ 30 OOS | <30 = no statistical claim |
| Recovery factor | ≥ 3 | NetPnL / MaxDD |

### 7.4 Pitfalls — Defenses Codified

| Pitfall | Defense |
|---------|---------|
| **Lookahead bias** | Strategy code only sees `Candle` rows where `IsClosed=1` AND `OpenTime + Interval ≤ replayClock`. Indicators recomputed bar-by-bar; never index into future. |
| **Survivorship bias** | Persist symbol delisting events in `Symbols.IsActive`; backtest universe = "active at point in time", not "active today". |
| **Overfitting** | Walk-forward + Deflated Sharpe + parameter ranges constrained; reject any setup with > 6 free parameters. |
| **Look-back bias** | Always use `WHERE OpenTime <= @asOf` in indicator queries during replay. |
| **Snooping bias** | Researcher records the number of strategies tested; DSR penalizes accordingly. |
| **Fee/slippage optimism** | Charge at least taker fee in backtests; add 2–5 bps slippage on every fill. |
| **Position-size cheating** | Recompute position size from simulated equity each trade — not from initial capital ([stoic.ai](https://stoic.ai/blog/backtesting-trading-strategies/)). |

### 7.5 Paper-Trading Phase

Before live capital:
1. **Binance testnet** (`testnet.binance.vision` for spot, `testnet.binancefuture.com` for futures — Binance.Net supports via `BinanceEnvironment.Testnet`) for at least 2 weeks running the full stack.
2. **Live with $500 sub-account** for 2–4 weeks (mainnet, small notional). This reveals real fee/latency/slippage that testnet cannot.
3. Only then scale to full account.

### 7.6 Monte Carlo Simulation

After WFA passes, run two Monte Carlo procedures:
1. **Trade reshuffle** (≥1,000 simulations): randomize trade order to expose path-dependency in drawdown. Report 5th/50th/95th percentile of MDD ([BuildAlpha guide](https://www.buildalpha.com/monte-carlo-simulation/)).
2. **Trade-skip stress** (10–15% random skips): simulates connectivity outages, missed alerts. Robust strategies tolerate this with <2× MDD inflation ([BacktestBase methodology](https://www.backtestbase.com/education/monte-carlo-stress-testing)).

Acceptance: 95th-percentile simulated MDD < 25% (i.e., even unlucky paths stay survivable on a 15% MDD budget).

---

## 8. Risk Management

### 8.1 Position Sizing Formulas

**Default: Fixed fractional, 1% risk per trade.** With $20,000 equity and risk per trade `R = 1%`:

```
risk_dollars     = equity * 0.01          // = $200
stop_distance    = abs(entry - stopLoss)
position_units   = risk_dollars / stop_distance
position_notional = position_units * entry
```

Numeric example: BTCUSDT entry $64,000, SL $63,200 (1.5×ATR = $800), $20K account:
```
position_units    = 200 / 800 = 0.25 BTC
position_notional = 0.25 * 64,000 = $16,000   // 80% of equity
```
On 2× futures leverage that is $8,000 margin (acceptable). On spot we'd cap at e.g. 50% of equity per single position regardless of stop math.

**Volatility-targeted overlay:** scale size inversely to current ATR vs its 50-bar median; if `atr14 / atr50_median > 1.4`, scale 0.7×; if `< 0.7`, scale 1.2× (capped). This dampens performance in vol explosions.

**Fractional Kelly (advanced, optional):** with edge `b` (avg win/avg loss) and probability `p`:
```
f_kelly  = p - (1 - p)/b
f_actual = 0.25 * f_kelly         // Quarter-Kelly (industry default for crypto)
```
Inputs **must** come from ≥100 actual closed trades per strategy, not backtest ([Altrady on fractional Kelly](https://www.altrady.com/blog/risk-management/kelly-criterion-crypto-position-sizing); [LBank guide](https://www.lbank.com/explore/mastering-the-kelly-criterion-for-smarter-crypto-risk-management)). Negative Kelly = stop trading that strategy.

### 8.2 Hard Risk Caps

| Constraint | Value | Enforcement |
|-----------|-------|-------------|
| Max risk per trade | 1% of equity | Risk Manager pre-trade check |
| Max concurrent positions | 4 (across all strategies) | Risk Manager |
| Max gross exposure | 200% of equity (futures, 2× total) | Risk Manager |
| Max single-symbol exposure | 50% of equity | Risk Manager |
| **Daily loss limit** | -3% realized+unrealized | **Circuit breaker: kill new entries; existing positions managed normally** |
| Weekly loss limit | -6% | Reduce next-week per-trade risk to 0.5% |
| Max drawdown from HWM | -10% → reduce sizing 50%; -15% → halt all entries | de-risking ladder |
| Max leverage (futures) | 3× (hard-coded; UI-set higher ignored) | |
| Funding-rate veto | Skip futures entries when |upcoming funding| > 0.05% if we'd be on paying side | |

This follows the standard 1-2-6 framework: 1% per trade, 2-3% daily, 6% monthly ([P&L Ledger](https://www.pnlledger.com/daily-loss-limits-weekly-max-drawdown-rules/); Alexander Elder's "6% rule" widely cited).

### 8.3 Correlation-Based Constraints

Cryptos (especially altcoins) typically show **+0.7 to +0.9 BTC correlation**, spiking to +0.9+ in crashes ([CalcBee](https://www.calcbee.com/calculators/crypto/risk/crypto-correlation-matrix-calculator/); [Sharpe terminal](https://www.sharpe.ai/correlation)). Holding "5 long ALTs" is effectively a single concentrated BTC bet.

Implementation:
1. Maintain rolling 30-day daily-return correlation matrix in Redis, refreshed nightly.
2. Group symbols by correlation cluster (Louvain or simple threshold > 0.7).
3. Enforce: **max 1 open position per cluster on the same side**, and **sum of correlated long notional ≤ 75% of equity**.
4. Stablecoins and BTC-shorts are treated as their own clusters and uncorrelated to long-alt cluster.

### 8.4 Drawdown-Based De-Risking Ladder

```csharp
public decimal RiskMultiplier(decimal currentDD) => currentDD switch
{
    >= -0.05m => 1.00m,    // < 5% DD: full size
    >= -0.10m => 0.50m,    // 5–10% DD: half size
    >= -0.15m => 0.25m,    // 10–15% DD: quarter size
    _         => 0.00m     // > 15% DD: HALT, manual review
};
```

Rationale: a 50% drawdown requires 100% recovery; staying small during pain protects the compound. Combined with the daily 3% circuit breaker, this gives layered protection: short-term variance (DLL), medium-term performance decay (DD ladder), and tail risk (kill-switch) ([JournalPlus on drawdown management](https://journalplus.co/metrics/maximum-drawdown/)).

### 8.5 Pseudocode — Risk Manager Gate

```csharp
public async Task<RiskDecision> ApproveAsync(Signal s, AccountSnapshot acct)
{
    if (acct.DailyPnlPct <= -0.03m) return Reject("DAILY_LOSS_LIMIT");
    if (acct.DrawdownPct <= -0.15m) return Reject("MAX_DRAWDOWN_HALT");
    if (acct.OpenPositions >= 4)    return Reject("MAX_CONCURRENT_POSITIONS");
    if (await ClusterAlreadyOpen(s.SymbolId, s.Side))
                                    return Reject("CORRELATION_CLUSTER_OCCUPIED");

    var kFactor = RiskMultiplier(acct.DrawdownPct);
    if (kFactor == 0m) return Reject("DERISK_HALT");

    var riskUsd = acct.EquityUsd * 0.01m * kFactor * VolAdjust(s);
    var stopDist = Math.Abs(s.EntryPrice - s.StopLoss);
    var qty = riskUsd / stopDist;
    qty = ClampToBinanceFilters(qty, s.Symbol);

    var notional = qty * s.EntryPrice;
    if (notional > 0.5m * acct.EquityUsd)        // single-symbol cap
        qty = (0.5m * acct.EquityUsd) / s.EntryPrice;

    if (acct.GrossExposure + notional > 2.0m * acct.EquityUsd)
                                    return Reject("GROSS_EXPOSURE");

    if (s.AccountType == AccountType.Futures && IsFundingHostile(s))
                                    return Reject("FUNDING_RATE_HOSTILE");

    return Approve(qty);
}
```

---

## 9. Deployment Plan

### 9.1 Hosting Choice

| Option | Pros | Cons | Verdict |
|--------|------|------|---------|
| Local Windows server | Cheapest, no egress fees | ISP/power risk, you operate it | OK for paper |
| **Azure VM (B2s/B2ms) in eu-central** | Low latency to Binance EU edge (~5–25ms), Microsoft ecosystem fit (SQL Server licensing, App Insights), 99.95% SLA | $30–80/month | **Recommended** for production |
| AWS EC2 in ap-northeast-1 (Tokyo) | Closest to Binance core matching engine in Tokyo (1–5ms) | More setup for SQL Server on AWS | Best latency, choose if <10ms matters |
| Binance VPS / colocation | Sub-ms | Premium pricing | Overkill for 5m–1h TF |

**Recommendation:** Azure VM (Standard_B2ms, 2 vCPU, 8 GB) in `northeurope` or AWS EC2 t3.medium in `ap-northeast-1`. Network latency to Binance is far below our bar-close cadence; this is not a HFT system.

### 9.2 Containerization

- Dockerfile based on `mcr.microsoft.com/dotnet/aspnet:8.0`. Multi-stage build, non-root user, ~120MB image.
- Compose stack: `tradingbot`, `seq` (logs), `redis`, `n8n`, optionally `prometheus + grafana`.
- SQL Server: prefer **Azure SQL Database (S2/S3)** over SQL in container — managed backups, point-in-time restore, encryption at rest.
- Secrets via Azure Key Vault or AWS Secrets Manager; **never** in env files committed to repo.

### 9.3 Worker Service / 24/7 Operation

`Microsoft.Extensions.Hosting.Worker` template + `Microsoft.Extensions.Hosting.WindowsServices` (or systemd unit on Linux). Each subsystem is a `BackgroundService`:

```csharp
builder.Services.AddHostedService<MarketDataIngestor>();
builder.Services.AddHostedService<CandlePersistor>();
builder.Services.AddHostedService<SignalEngine>();
builder.Services.AddHostedService<ExecutionEngine>();
builder.Services.AddHostedService<UserDataStreamConsumer>();
builder.Services.AddHostedService<ReconciliationService>();
```

Restart-on-failure: `Restart=on-failure RestartSec=10s` (systemd) or Recovery tab (Windows Service). Health endpoint exposed for Docker/Azure to probe.

### 9.4 Scheduling

**Recommendation: Quartz.NET for cron-like jobs, Hangfire for ad-hoc enqueueing.** Quartz wins on precision scheduling (timezones, calendars, `[DisallowConcurrentExecution]`) and clustering with persistent job stores in SQL Server ([code-maze comparison](https://code-maze.com/chsarp-the-differences-between-quartz-net-and-hangfire/); [WireFuture](https://wirefuture.com/post/background-jobs-in-net-hangfire-vs-quartz-vs-worker-services)). Hangfire's dashboard is nicer for ops, so for visibility-heavy queues you can use Hangfire + Quartz side-by-side.

Scheduled jobs:
| Job | Cadence | Engine |
|-----|---------|--------|
| Candle backfill (gap fill) | every 5 min | Quartz |
| Reference data refresh (`exchangeInfo`) | daily 00:05 UTC | Quartz |
| Account snapshot | every 1 min | Quartz |
| Partition maintenance | monthly 01:00 UTC on day 1 | Quartz |
| Walk-forward batch | weekly Sunday 02:00 UTC | Hangfire |
| Claude weekly journal | weekly Sunday 06:00 UTC | Hangfire (batch API) |
| Correlation matrix recompute | nightly | Quartz |

### 9.5 Logging & Monitoring

**Logging:** Serilog with sinks to Console (Docker), File (rolling), and **Seq** (structured queryable UI) ([C# Corner Serilog+Seq guide](https://www.c-sharpcorner.com/article/enhancing-application-insights-with-serilog-and-seq/)). For Azure deployments add `Serilog.Sinks.ApplicationInsights` for KQL-based correlation across traces.

`appsettings.json`:
```json
"Serilog": {
  "Using": ["Serilog.Sinks.Seq","Serilog.Sinks.File","Serilog.Sinks.ApplicationInsights"],
  "MinimumLevel": { "Default": "Information",
                    "Override": { "Microsoft": "Warning","Binance.Net": "Information" }},
  "WriteTo": [
    { "Name": "Seq", "Args": { "serverUrl": "http://seq:5341" }},
    { "Name": "File","Args": { "path":"logs/bot-.log","rollingInterval":"Day",
                               "retainedFileCountLimit": 30 }}
  ],
  "Enrich": ["FromLogContext","WithMachineName","WithThreadId","WithEnvironmentName"]
}
```

Every signal/order/risk-event is logged with the same `CorrelationId` (the SignalId) so a Seq query `CorrelationId = 1234` returns the entire decision trail.

**Metrics:** `prometheus-net` exporter on `/metrics` exposing:
- `bot_signals_total{strategy,symbol,side}`
- `bot_orders_submitted_total{symbol,type}`, `bot_orders_filled_total`, `bot_orders_rejected_total{reason}`
- `bot_position_pnl_usd{symbol}` (gauge)
- `bot_account_equity_usd` (gauge), `bot_account_drawdown_pct`
- `bot_ai_calls_total{purpose,result}`, `bot_ai_cost_usd_total`
- `bot_ws_reconnects_total`
- `bot_strategy_latency_ms_bucket` (histogram, bar-close → order submit)

Grafana dashboards for: equity curve, drawdown, fill latency, WS health, AI cost.

### 9.6 Alerting

Telegram bot via n8n is the simplest robust path:
- Bot in C# pushes alert events to a Redis stream `alerts:critical` / `alerts:warn` / `alerts:info`.
- n8n workflow consumes the stream and forwards to Telegram chat (one chat per severity), with rate-limiting and message-deduplication ([Automations-Project n8n Telegram template](https://github.com/Automations-Project/n8n-crypto-market-alert-system-with-binance-and-telegram-integration)).
- Critical events: API ban, daily loss limit, max DD halt, WS disconnect >60s, any rejected order with non-business code.

Email backup via SendGrid or SMTP for digest summary every 06:00 UTC.

### 9.7 Disaster Recovery & Failover

- **State recovery on cold start:** read all non-terminal orders from `dbo.Orders`, query Binance via `GET /openOrders` and `GET /allOrders` for last 24h, reconcile diffs, replay missed userData via `GET /userTrades`. The bot must be restart-safe at any moment.
- **Position bootstrap:** on startup, fetch account positions from Binance and confirm against `dbo.Positions WHERE Status='OPEN'`; mismatch raises CRITICAL and goes to safe-mode (no new entries until reconciled).
- **Database backups:** Azure SQL automated PITR (35-day retention), plus weekly export to blob.
- **Geographic failover (overkill but documented):** secondary Azure VM in another region kept warm via leader-election in SQL Server table `dbo.Leadership`; only the leader places orders. Heartbeat check 5s; takeover after 30s missed beats.
- **API key safety:** keys are **trading-only** (no withdraw permission), IP-whitelisted. Two key pairs: one for read-only health (separate process), one for trading.

---

## 10. Step-by-Step Implementation Roadmap

### Phase 1 — MVP (4–6 weeks)

**Objective:** single strategy, single pair, paper trading on testnet, basic SL/TP.

| Week | Deliverable |
|------|-------------|
| 1 | .NET 8 solution scaffold; `Binance.Net` 12.x integration; testnet config; `Symbols`/`Candles` tables; REST kline backfill for BTCUSDT 1h |
| 2 | WebSocket kline + userData consumers; `CandlePersistor` with idempotent upsert; Skender indicator wiring (ATR, EMA, ADX) |
| 3 | One strategy (TREND_EMA_ADX); `Signals` table; rule-based regime classifier; simple position sizer (1% risk) |
| 4 | Execution engine: limit/market entries, STOP_MARKET/TAKE_PROFIT_MARKET exits; OrderStateMachine; userData reconciliation; idempotent `clientOrderId` |
| 5 | Serilog→Seq, Telegram bot via n8n, basic Grafana dashboard; restart-safety reconciliation; Polly retry pipeline |
| 6 | Run on Binance testnet 2 weeks; tune parameters; close Phase 1 |

**Exit criteria:** Bot runs unattended for 7 days on testnet, no manual intervention, all orders correctly state-tracked, full audit trail in SQL.

### Phase 2 — Optimization (6–8 weeks)

**Objective:** multi-strategy, multi-pair, AI sentiment integration, walk-forward validation.

| Week | Deliverable |
|------|-------------|
| 1–2 | Add BREAKOUT_DON and MR_BB_VWAP strategies; regime selector; per-strategy parameter config |
| 2–3 | Backtester (replay engine reusing live indicator/signal modules); WFA runner (Hangfire); reports with Sharpe, Sortino, Calmar, Deflated Sharpe |
| 3–4 | Monte Carlo (reshuffle + skip stress) report; reject-or-proceed gates baked into deployment pipeline |
| 4–5 | Claude API integration: news sentiment + regime confirm + setup confirmation; `AiInteractions` table + caching; cost monitoring |
| 5–6 | Multi-symbol expansion (BTC, ETH, SOL, BNB; add 2 alts after correlation analysis); correlation-cluster constraint |
| 6–7 | XGBoost local filter (XGBoostSharp); weekly retrain Hangfire job |
| 7–8 | Paper-trade $500 mainnet sub-account 2 weeks; tune; close Phase 2 |

**Exit criteria:** All three strategies pass WFA with OOS Deflated Sharpe p<0.05 on at least BTC/ETH; Monte Carlo 95th-pct MDD < 25%; live $500 paper P&L within 1σ of backtest expectation.

### Phase 3 — Production (4–6 weeks)

**Objective:** full risk management, monitoring, live trading with controlled scaling.

| Week | Deliverable |
|------|-------------|
| 1 | Full risk manager: daily loss limit, drawdown ladder, correlation gates, funding-aware exits, kill-switch |
| 2 | Monitoring polish: Prometheus + Grafana SLO dashboard; alert routes (CRITICAL → Telegram + email + SMS-via-Twilio optional); automated daily P&L digest |
| 2–3 | DR procedures: backup/restore drills; cold-start reconciliation tested by chaos test (kill -9 mid-trade); secondary VM standby |
| 3 | Partition maintenance jobs in production; columnstore archive switching for >6mo candles |
| 3–4 | Go live with $2,000 (10% of target capital). Run 2 weeks. |
| 4–5 | Scale to $10,000 if metrics align with expectations (Sharpe within 0.7× of backtest; MDD < expected). |
| 5–6 | Scale to $20K–$50K. Weekly Claude post-trade journal becomes input to next iteration. |

**Exit criteria for going live at full size:** 4 consecutive weeks at lower capital meeting:
- Realized monthly Sharpe ≥ 1.0
- Realized MDD ≤ 80% of backtest MDD
- No CRITICAL incidents
- No reconciliation drift > $5

### Ongoing (Post-Phase-3)

- Quarterly walk-forward refresh; if OOS DSR drops below threshold, retire/refit the strategy.
- Monthly correlation matrix and exposure cap review.
- Weekly Claude journal → feeds parameter-search backlog.
- Continuous monitoring of Binance API changelog for breaking changes ([changelog](https://developers.binance.com/docs/binance-spot-api-docs)).

---

## Appendix A — Key Library/Reference Versions Used in Design

| Component | Version | Source |
|-----------|---------|--------|
| Binance.Net (JKorf) | 12.11.x (stable Dec 2026) | [GitHub](https://github.com/JKorf/Binance.Net) — supports REST + WebSocket spot/UM/CM futures, client-side rate limiting, automatic WS reconnection, local order book |
| Skender.Stock.Indicators | 2.7.x stable / 3.0 preview (streaming hubs) | [stockindicators.dev](https://daveskender.github.io/Stock.Indicators/) — 100+ indicators incl. ATR, ADX, Donchian, ChandelierExit |
| Polly | v8 ResiliencePipeline | [pollydocs.org](https://www.pollydocs.org/strategies/retry.html) — retry with decorrelated jitter recommended |
| Serilog + Seq | latest | structured logging stack |
| Quartz.NET | 3.x with SqlServerJobStore | for clustered scheduling |
| Hangfire | latest with SqlServerStorage | for background jobs with dashboard |
| XGBoostSharp | latest | [GitHub](https://github.com/mdabros/XGBoostSharp) — .NET wrapper for XGBoost; `XGBClassifier`/`XGBRegressor` |
| Anthropic Claude | Sonnet 4.5 (`claude-sonnet-4-5-20250929`) | $3/$15 per MTok, prompt caching at 10% read price |
| n8n | self-hosted | for cross-system glue + Telegram + CryptoPanic |

## Appendix B — Critical Operational Numbers to Remember

| Item | Value |
|------|-------|
| Binance Spot API request weight limit | 6,000/min per IP ([docs](https://developers.binance.com/docs/binance-spot-api-docs/websocket-api/rate-limits)) |
| Binance Futures USDⓈ-M default IP rate limit | 2,400 req/min; default order limit 1,200/min, 300/10s ([Binance Futures rate limits](https://www.binance.com/en/support/faq/rate-limits-on-binance-futures-281596e222414cdd9051664ea621cdc3)) |
| Binance Spot fees | 0.10% maker / 0.10% taker base; -25% with BNB |
| Binance USDⓈ-M Futures fees | 0.02% maker / 0.05% taker base; -10% with BNB ([Cryptopotato](https://cryptopotato.com/binance-fees/)) |
| Binance Futures funding interval | every 8h (some 4h); funding capped via ±0.05% premium clamp ([Binance funding](https://www.binance.com/en/blog/futures/what-is-futures-funding-rate-and-why-it-matters-421499824684903247)) |
| WS limit incoming msgs | Spot 5/s, Futures 10/s; max 1,024 streams per connection |
| listenKey TTL | 60 minutes; PUT to extend ([Binance docs](https://developers.binance.com/docs/derivatives/usds-margined-futures/user-data-streams)) |
| HTTP 429 response | back off, respect `retryAfter` field |
| HTTP 418 response | IP banned (2 min → 3 days); kill-switch immediately |

## Appendix C — Tradeoff Summary for Senior Reviewers

| Decision | Tradeoff | Default chosen | Rationale |
|----------|----------|----------------|-----------|
| Modular monolith vs microservices | Operational complexity vs scalability | Modular monolith | Account size; in-process channels minimize hot-path latency |
| In-process channels vs RabbitMQ | Latency vs durability | In-process channels | Trading hot path; SQL Server is audit |
| Custom backtester vs library | Build effort vs reuse | Custom (reuses live code) | Eliminates sim-prod drift |
| Quartz vs Hangfire | Precision vs UX | Quartz primary, Hangfire for ops | Quartz `[DisallowConcurrentExecution]` is critical for scheduling |
| Claude vs local ML for setup confirm | Quality + reasoning vs latency + cost | Claude for borderline only, local XGBoost as primary filter | Claude is 1000× slower; XGBoost handles volume |
| Fixed fractional vs Kelly | Simplicity vs theoretical optimum | Fixed 1% (Quarter-Kelly later) | Crypto distribution fat tails make full-Kelly catastrophic |
| Spot vs futures-only | Simplicity vs short-side + leverage | Both, futures with strict ≤3× cap | Need short capability; cap protects from blowup |
| ATR-based vs fixed SL/TP | Adaptivity vs simplicity | ATR-based always | Standard quant practice; volatility regimes matter in crypto |

---

This blueprint is intended to be directly executable by a 2–3 person senior dev team over ~16–20 weeks to a fully operational, audit-grade automated trading system on Binance, with capacity to scale to multi-strategy/multi-symbol portfolios, while keeping the AI layer cost-controlled and the risk discipline non-negotiable.