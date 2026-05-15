-- =============================================================================
-- 009_backtest.sql
-- §10 Backtester output schema. Lives in a separate `bt` schema so backtest
-- traffic never mixes with live `dbo.*` rows. Mirror tables carry an extra
-- BacktestRunId so a single SQL pass can join trades to their run, and
-- foreign keys point at dbo.Symbols (read-only reference data).
-- All times DATETIME2(3) UTC; prices/qty DECIMAL(38,18); USD DECIMAL(38,8).
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'bt') IS NULL EXEC(N'CREATE SCHEMA bt');
GO

-- ---------------------------------------------------------------------------
-- dbo.BacktestRuns — one row per `bt run`, `wfa` fold, or `mc` simulation.
-- Lives in dbo so the FK targets (referenced by bt.* mirror tables) survive
-- a `DROP SCHEMA bt` cleanup.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.BacktestRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BacktestRuns
    (
        BacktestRunId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BacktestRuns PRIMARY KEY,
        RunKind             VARCHAR(16)    NOT NULL,            -- RUN | WFA_IS | WFA_OOS | MC_RESHUFFLE | MC_SKIP
        ParentRunId         BIGINT         NULL,                -- WFA folds → parent WFA run; MC sims → backtest base
        Strategy            VARCHAR(32)    NOT NULL,            -- BREAKOUT_DON | MR_BB_VWAP | TREND_EMA_ADX | ALL
        Symbols             NVARCHAR(256)  NOT NULL,            -- comma-separated, alphabetised
        AccountType         VARCHAR(8)     NOT NULL,            -- SPOT | UMFUT
        FromUtc             DATETIME2(3)   NOT NULL,
        ToUtc               DATETIME2(3)   NOT NULL,
        StartingEquityUsd   DECIMAL(38,8)  NOT NULL,
        Seed                BIGINT         NOT NULL,            -- RNG seed for slippage / MC reshuffle (determinism)
        ParametersJson      NVARCHAR(MAX)  NULL,                -- frozen strategy/risk/exec config used for this run
        FeeMakerBps         DECIMAL(10,4)  NOT NULL,            -- e.g. 2.0 = 0.02%
        FeeTakerBps         DECIMAL(10,4)  NOT NULL,
        SlippageModelVersion VARCHAR(16)   NOT NULL,            -- DefaultSlippageModel.ModelVersion at run time
        Status              VARCHAR(16)    NOT NULL,            -- PENDING | RUNNING | COMPLETED | FAILED
        StartedAt           DATETIME2(3)   NOT NULL CONSTRAINT DF_BacktestRuns_StartedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt         DATETIME2(3)   NULL,
        DurationMs          BIGINT         NULL,
        BarsReplayed        BIGINT         NULL,
        TradesGenerated     INT            NULL,
        FinalEquityUsd      DECIMAL(38,8)  NULL,
        MetricsJson         NVARCHAR(MAX)  NULL,                -- final metrics blob (Sharpe, MDD, etc.)
        ErrorMessage        NVARCHAR(2000) NULL,
        Notes               NVARCHAR(512)  NULL,
        CONSTRAINT FK_BacktestRuns_Parent FOREIGN KEY (ParentRunId) REFERENCES dbo.BacktestRuns(BacktestRunId)
    );

    CREATE INDEX IX_BacktestRuns_Status ON dbo.BacktestRuns(Status, StartedAt DESC);
    CREATE INDEX IX_BacktestRuns_Parent ON dbo.BacktestRuns(ParentRunId) WHERE ParentRunId IS NOT NULL;
END;
GO

-- ---------------------------------------------------------------------------
-- bt.Signals — mirror of dbo.Signals + RunId. No FK to dbo.Signals (parallel).
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.Signals', N'U') IS NULL
BEGIN
    CREATE TABLE bt.Signals
    (
        SignalId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtSignals PRIMARY KEY,
        BacktestRunId   BIGINT         NOT NULL,
        SymbolId        INT            NOT NULL,
        Strategy        VARCHAR(32)    NOT NULL,
        [Interval]      VARCHAR(8)     NOT NULL,
        BarOpenTime     DATETIME2(3)   NOT NULL,
        Side            CHAR(4)        NOT NULL,
        EntryPrice      DECIMAL(38,18) NOT NULL,
        StopLoss        DECIMAL(38,18) NOT NULL,
        TakeProfit      DECIMAL(38,18) NOT NULL,
        AtrValue        DECIMAL(38,18) NULL,
        Regime          VARCHAR(16)    NULL,
        Confidence      DECIMAL(5,2)   NOT NULL,
        Status          VARCHAR(16)    NOT NULL,
        Reason          NVARCHAR(512)  NULL,
        CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_BtSignals_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_BtSignals_Run     FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_BtSignals_Symbols FOREIGN KEY (SymbolId)      REFERENCES dbo.Symbols(SymbolId)
    );
    CREATE INDEX IX_BtSignals_Run ON bt.Signals(BacktestRunId, BarOpenTime);
END;
GO

-- ---------------------------------------------------------------------------
-- bt.Orders
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.Orders', N'U') IS NULL
BEGIN
    CREATE TABLE bt.Orders
    (
        OrderId             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtOrders PRIMARY KEY,
        BacktestRunId       BIGINT         NOT NULL,
        SignalId            BIGINT         NULL,                -- bt.Signals(SignalId)
        SymbolId            INT            NOT NULL,
        AccountType         VARCHAR(8)     NOT NULL,
        ClientOrderId       VARCHAR(48)    NOT NULL,
        ExchangeOrderId     BIGINT         NULL,                -- simulated monotonically
        OrderType           VARCHAR(24)    NOT NULL,
        Side                CHAR(4)        NOT NULL,
        PositionSide        VARCHAR(8)     NULL,
        Quantity            DECIMAL(38,18) NOT NULL,
        Price               DECIMAL(38,18) NULL,
        StopPrice           DECIMAL(38,18) NULL,
        TimeInForce         VARCHAR(8)     NULL,
        ReduceOnly          BIT            NOT NULL CONSTRAINT DF_BtOrders_ReduceOnly DEFAULT (0),
        Status              VARCHAR(24)    NOT NULL,
        FilledQty           DECIMAL(38,18) NOT NULL CONSTRAINT DF_BtOrders_FilledQty DEFAULT (0),
        AvgFillPrice        DECIMAL(38,18) NULL,
        CommissionPaid      DECIMAL(38,18) NOT NULL CONSTRAINT DF_BtOrders_CommissionPaid DEFAULT (0),
        CommissionAsset     VARCHAR(16)    NULL,
        SubmittedAt         DATETIME2(3)   NOT NULL,
        LastUpdatedAt       DATETIME2(3)   NOT NULL,
        Notes               NVARCHAR(512)  NULL,
        CONSTRAINT UQ_BtOrders_Run_ClientOrderId UNIQUE (BacktestRunId, ClientOrderId),
        CONSTRAINT FK_BtOrders_Run     FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_BtOrders_Symbols FOREIGN KEY (SymbolId)      REFERENCES dbo.Symbols(SymbolId)
    );
    CREATE INDEX IX_BtOrders_Run ON bt.Orders(BacktestRunId, SubmittedAt);
END;
GO

-- ---------------------------------------------------------------------------
-- bt.Fills
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.Fills', N'U') IS NULL
BEGIN
    CREATE TABLE bt.Fills
    (
        FillId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtFills PRIMARY KEY,
        BacktestRunId   BIGINT         NOT NULL,
        OrderId         BIGINT         NOT NULL,                -- bt.Orders(OrderId)
        TradeId         BIGINT         NOT NULL,
        Quantity        DECIMAL(38,18) NOT NULL,
        Price           DECIMAL(38,18) NOT NULL,
        Commission      DECIMAL(38,18) NOT NULL,
        CommissionAsset VARCHAR(16)    NOT NULL,
        IsMaker         BIT            NOT NULL,
        TradeTime       DATETIME2(3)   NOT NULL,
        CONSTRAINT UQ_BtFills_Order_Trade UNIQUE (OrderId, TradeId),
        CONSTRAINT FK_BtFills_Run FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId)
    );
    CREATE INDEX IX_BtFills_Run ON bt.Fills(BacktestRunId, TradeTime);
END;
GO

-- ---------------------------------------------------------------------------
-- bt.Positions
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.Positions', N'U') IS NULL
BEGIN
    CREATE TABLE bt.Positions
    (
        PositionId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtPositions PRIMARY KEY,
        BacktestRunId   BIGINT         NOT NULL,
        SymbolId        INT            NOT NULL,
        AccountType     VARCHAR(8)     NOT NULL,
        Side            CHAR(5)        NOT NULL,
        EntrySignalId   BIGINT         NULL,
        EntryOrderId    BIGINT         NULL,
        Quantity        DECIMAL(38,18) NOT NULL,
        AvgEntryPrice   DECIMAL(38,18) NOT NULL,
        StopLoss        DECIMAL(38,18) NOT NULL,
        TakeProfit      DECIMAL(38,18) NOT NULL,
        InitialRiskUsd  DECIMAL(38,8)  NOT NULL,
        OpenedAt        DATETIME2(3)   NOT NULL,
        ClosedAt        DATETIME2(3)   NULL,
        ClosePrice      DECIMAL(38,18) NULL,
        RealizedPnlUsd  DECIMAL(38,8)  NULL,
        Status          VARCHAR(16)    NOT NULL,
        CONSTRAINT FK_BtPositions_Run     FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_BtPositions_Symbols FOREIGN KEY (SymbolId)      REFERENCES dbo.Symbols(SymbolId)
    );
    CREATE INDEX IX_BtPositions_Run ON bt.Positions(BacktestRunId, OpenedAt);
END;
GO

-- ---------------------------------------------------------------------------
-- bt.TradeHistory — denormalised closed-trade fact (the primary input to MC).
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.TradeHistory', N'U') IS NULL
BEGIN
    CREATE TABLE bt.TradeHistory
    (
        TradeHistoryId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtTradeHistory PRIMARY KEY,
        BacktestRunId   BIGINT         NOT NULL,
        PositionId      BIGINT         NOT NULL,                -- bt.Positions
        SymbolId        INT            NOT NULL,
        Strategy        VARCHAR(32)    NOT NULL,
        Regime          VARCHAR(16)    NULL,
        Side            CHAR(5)        NOT NULL,
        EntryTime       DATETIME2(3)   NOT NULL,
        ExitTime        DATETIME2(3)   NOT NULL,
        HoldingMinutes  INT            NOT NULL,
        EntryPrice      DECIMAL(38,18) NOT NULL,
        ExitPrice       DECIMAL(38,18) NOT NULL,
        Quantity        DECIMAL(38,18) NOT NULL,
        GrossPnlUsd     DECIMAL(38,8)  NOT NULL,
        FeesUsd         DECIMAL(38,8)  NOT NULL,
        NetPnlUsd       DECIMAL(38,8)  NOT NULL,
        R_Multiple      DECIMAL(10,4)  NOT NULL,
        ExitReason      VARCHAR(24)    NOT NULL,
        CONSTRAINT FK_BtTH_Run     FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_BtTH_Symbols FOREIGN KEY (SymbolId)      REFERENCES dbo.Symbols(SymbolId)
    );
    CREATE INDEX IX_BtTH_Run ON bt.TradeHistory(BacktestRunId, ExitTime);
    CREATE INDEX IX_BtTH_Run_Strategy ON bt.TradeHistory(BacktestRunId, Strategy);
    CREATE INDEX IX_BtTH_Run_Regime ON bt.TradeHistory(BacktestRunId, Regime);
END;
GO

-- ---------------------------------------------------------------------------
-- bt.AccountSnapshots — equity curve sample points (one per replay step).
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'bt.AccountSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE bt.AccountSnapshots
    (
        SnapshotId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BtAccountSnapshots PRIMARY KEY,
        BacktestRunId   BIGINT         NOT NULL,
        AccountType     VARCHAR(8)     NOT NULL,
        SnapshotTime    DATETIME2(3)   NOT NULL,
        EquityUsd       DECIMAL(38,8)  NOT NULL,
        AvailableUsd    DECIMAL(38,8)  NOT NULL,
        UnrealizedPnl   DECIMAL(38,8)  NOT NULL,
        OpenPositions   INT            NOT NULL,
        GrossExposure   DECIMAL(38,8)  NOT NULL,
        NetExposure     DECIMAL(38,8)  NOT NULL,
        Drawdown        DECIMAL(7,4)   NOT NULL,
        CONSTRAINT FK_BtAS_Run FOREIGN KEY (BacktestRunId) REFERENCES dbo.BacktestRuns(BacktestRunId)
    );
    CREATE INDEX IX_BtAS_Run_Time ON bt.AccountSnapshots(BacktestRunId, SnapshotTime);
END;
GO

-- ---------------------------------------------------------------------------
-- dbo.WalkForwardFolds — a walk-forward run is a parent BacktestRun with
-- two child BacktestRuns per fold (IS + OOS). This table stores per-fold
-- header metrics so the verdict query (Sharpe-OOS ≥ 0.6 × Sharpe-IS in
-- ≥70% of folds) is one row scan.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.WalkForwardFolds', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WalkForwardFolds
    (
        WfaFoldId           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WalkForwardFolds PRIMARY KEY,
        ParentRunId         BIGINT         NOT NULL,            -- dbo.BacktestRuns where RunKind='WFA'
        FoldIndex           INT            NOT NULL,            -- 0-based
        IsFromUtc           DATETIME2(3)   NOT NULL,
        IsToUtc             DATETIME2(3)   NOT NULL,
        OosFromUtc          DATETIME2(3)   NOT NULL,
        OosToUtc            DATETIME2(3)   NOT NULL,
        IsRunId             BIGINT         NOT NULL,            -- bt.* rows for IS fold
        OosRunId            BIGINT         NOT NULL,            -- bt.* rows for OOS fold
        IsSharpe            DECIMAL(10,4)  NULL,
        OosSharpe           DECIMAL(10,4)  NULL,
        IsCalmar            DECIMAL(10,4)  NULL,
        OosCalmar           DECIMAL(10,4)  NULL,
        IsMaxDdPct          DECIMAL(10,4)  NULL,
        OosMaxDdPct         DECIMAL(10,4)  NULL,
        IsTradeCount        INT            NULL,
        OosTradeCount       INT            NULL,
        BestParametersJson  NVARCHAR(MAX)  NULL,                -- frozen optimum from IS grid search
        AcceptanceMet       BIT            NULL,                -- OOS Sharpe ≥ 0.6 × IS Sharpe
        CONSTRAINT FK_WFA_Parent  FOREIGN KEY (ParentRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_WFA_IsRun   FOREIGN KEY (IsRunId)     REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT FK_WFA_OosRun  FOREIGN KEY (OosRunId)    REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT UQ_WFA_Parent_Fold UNIQUE (ParentRunId, FoldIndex)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- dbo.MonteCarloResults — one row per simulation (1,000 reshuffles or 100 skips).
-- Aggregate quantiles (5/50/95) are computed at report time from this table.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.MonteCarloResults', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MonteCarloResults
    (
        McResultId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MonteCarloResults PRIMARY KEY,
        ParentRunId     BIGINT         NOT NULL,                -- the original RUN that supplied trades
        SimulationKind  VARCHAR(16)    NOT NULL,                -- RESHUFFLE | SKIP
        Iteration       INT            NOT NULL,                -- 0 .. N-1
        Seed            BIGINT         NOT NULL,
        SkipFraction    DECIMAL(7,4)   NULL,                    -- non-null for SKIP
        FinalEquityUsd  DECIMAL(38,8)  NOT NULL,
        MaxDrawdownPct  DECIMAL(10,4)  NOT NULL,
        Sharpe          DECIMAL(10,4)  NULL,
        TradesUsed      INT            NOT NULL,
        CONSTRAINT FK_MC_Parent FOREIGN KEY (ParentRunId) REFERENCES dbo.BacktestRuns(BacktestRunId),
        CONSTRAINT UQ_MC_Parent_Kind_Iter UNIQUE (ParentRunId, SimulationKind, Iteration)
    );
    CREATE INDEX IX_MC_Parent_Kind ON dbo.MonteCarloResults(ParentRunId, SimulationKind);
END;
GO
