-- =============================================================================
-- 001_init_schema.sql
-- Reference and trading entity tables. No partitioning here (see 002).
-- All times DATETIME2(3) UTC. Prices/qty DECIMAL(38,18). USD amounts DECIMAL(38,8).
-- Idempotent: every CREATE is guarded by an existence check so DbUp re-runs are
-- safe even outside its SchemaVersions tracking (defence in depth).
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- Symbols
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Symbols', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Symbols
    (
        SymbolId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Symbols PRIMARY KEY,
        Exchange        VARCHAR(16)    NOT NULL,
        Symbol          VARCHAR(32)    NOT NULL,
        BaseAsset       VARCHAR(16)    NOT NULL,
        QuoteAsset      VARCHAR(16)    NOT NULL,
        TickSize        DECIMAL(38,18) NOT NULL,
        StepSize        DECIMAL(38,18) NOT NULL,
        MinNotional     DECIMAL(38,18) NOT NULL,
        IsActive        BIT            NOT NULL CONSTRAINT DF_Symbols_IsActive DEFAULT (1),
        UpdatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Symbols_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_Symbols_Exchange_Symbol UNIQUE (Exchange, Symbol)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- Signals
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Signals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Signals
    (
        SignalId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Signals PRIMARY KEY,
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
        SentimentScore  DECIMAL(5,2)   NULL,
        AiConfidence    DECIMAL(5,2)   NULL,
        Confidence      DECIMAL(5,2)   NOT NULL,
        Status          VARCHAR(16)    NOT NULL,
        Reason          NVARCHAR(512)  NULL,
        CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Signals_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Signals_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- Orders
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Orders
    (
        OrderId             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
        SignalId            BIGINT         NULL,
        SymbolId            INT            NOT NULL,
        AccountType         VARCHAR(8)     NOT NULL,
        ClientOrderId       VARCHAR(36)    NOT NULL,
        ExchangeOrderId     BIGINT         NULL,
        OrderType           VARCHAR(24)    NOT NULL,
        Side                CHAR(4)        NOT NULL,
        PositionSide        VARCHAR(8)     NULL,
        Quantity            DECIMAL(38,18) NOT NULL,
        Price               DECIMAL(38,18) NULL,
        StopPrice           DECIMAL(38,18) NULL,
        TimeInForce         VARCHAR(8)     NULL,
        ReduceOnly          BIT            NOT NULL CONSTRAINT DF_Orders_ReduceOnly DEFAULT (0),
        Status              VARCHAR(24)    NOT NULL,
        FilledQty           DECIMAL(38,18) NOT NULL CONSTRAINT DF_Orders_FilledQty DEFAULT (0),
        AvgFillPrice        DECIMAL(38,18) NULL,
        CommissionPaid      DECIMAL(38,18) NOT NULL CONSTRAINT DF_Orders_CommissionPaid DEFAULT (0),
        CommissionAsset     VARCHAR(16)    NULL,
        SubmittedAt         DATETIME2(3)   NOT NULL CONSTRAINT DF_Orders_SubmittedAt DEFAULT (SYSUTCDATETIME()),
        LastUpdatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Orders_LastUpdatedAt DEFAULT (SYSUTCDATETIME()),
        Notes               NVARCHAR(512)  NULL,
        CONSTRAINT UQ_Orders_ClientOrderId UNIQUE (ClientOrderId),
        CONSTRAINT FK_Orders_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT FK_Orders_Signals FOREIGN KEY (SignalId) REFERENCES dbo.Signals(SignalId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- Fills
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Fills', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Fills
    (
        FillId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Fills PRIMARY KEY,
        OrderId         BIGINT         NOT NULL,
        TradeId         BIGINT         NOT NULL,
        Quantity        DECIMAL(38,18) NOT NULL,
        Price           DECIMAL(38,18) NOT NULL,
        Commission      DECIMAL(38,18) NOT NULL,
        CommissionAsset VARCHAR(16)    NOT NULL,
        IsMaker         BIT            NOT NULL,
        TradeTime       DATETIME2(3)   NOT NULL,
        CONSTRAINT UQ_Fills_Order_Trade UNIQUE (OrderId, TradeId),
        CONSTRAINT FK_Fills_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(OrderId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- Positions
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Positions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Positions
    (
        PositionId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Positions PRIMARY KEY,
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
        CONSTRAINT FK_Positions_Symbols     FOREIGN KEY (SymbolId)      REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT FK_Positions_EntrySignal FOREIGN KEY (EntrySignalId) REFERENCES dbo.Signals(SignalId),
        CONSTRAINT FK_Positions_EntryOrder  FOREIGN KEY (EntryOrderId)  REFERENCES dbo.Orders(OrderId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- TradeHistory (denormalised closed-trade fact)
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.TradeHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TradeHistory
    (
        TradeHistoryId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TradeHistory PRIMARY KEY,
        PositionId      BIGINT         NOT NULL,
        SymbolId        INT            NOT NULL,
        Strategy        VARCHAR(32)    NOT NULL,
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
        CONSTRAINT FK_TH_Positions FOREIGN KEY (PositionId) REFERENCES dbo.Positions(PositionId),
        CONSTRAINT FK_TH_Symbols   FOREIGN KEY (SymbolId)   REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- AccountSnapshots
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.AccountSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountSnapshots
    (
        SnapshotId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountSnapshots PRIMARY KEY,
        AccountType     VARCHAR(8)    NOT NULL,
        SnapshotTime    DATETIME2(3)  NOT NULL,
        EquityUsd       DECIMAL(38,8) NOT NULL,
        AvailableUsd    DECIMAL(38,8) NOT NULL,
        UnrealizedPnl   DECIMAL(38,8) NOT NULL,
        OpenPositions   INT           NOT NULL,
        GrossExposure   DECIMAL(38,8) NOT NULL,
        NetExposure     DECIMAL(38,8) NOT NULL,
        Drawdown        DECIMAL(7,4)  NOT NULL
    );
END;
GO

-- ---------------------------------------------------------------------------
-- RiskEvents
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.RiskEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RiskEvents
    (
        RiskEventId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RiskEvents PRIMARY KEY,
        EventTime       DATETIME2(3)  NOT NULL CONSTRAINT DF_RiskEvents_EventTime DEFAULT (SYSUTCDATETIME()),
        EventType       VARCHAR(32)   NOT NULL,
        Severity        VARCHAR(8)    NOT NULL,
        SymbolId        INT           NULL,
        SignalId        BIGINT        NULL,
        OrderId         BIGINT        NULL,
        Payload         NVARCHAR(MAX) NULL,
        Acted           BIT           NOT NULL CONSTRAINT DF_RiskEvents_Acted DEFAULT (0),
        CONSTRAINT FK_Risk_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT FK_Risk_Signals FOREIGN KEY (SignalId) REFERENCES dbo.Signals(SignalId),
        CONSTRAINT FK_Risk_Orders  FOREIGN KEY (OrderId)  REFERENCES dbo.Orders(OrderId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- AiInteractions
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.AiInteractions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiInteractions
    (
        AiInteractionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiInteractions PRIMARY KEY,
        Purpose         VARCHAR(32)   NOT NULL,
        Model           VARCHAR(48)   NOT NULL,
        InputHash       CHAR(64)      NOT NULL,
        InputJson       NVARCHAR(MAX) NOT NULL,
        OutputJson      NVARCHAR(MAX) NULL,
        InputTokens     INT           NULL,
        OutputTokens    INT           NULL,
        LatencyMs       INT           NULL,
        CostUsd         DECIMAL(10,6) NULL,
        CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_AiInteractions_CreatedAt DEFAULT (SYSUTCDATETIME())
    );
END;
GO
