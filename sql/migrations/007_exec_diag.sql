-- =============================================================================
-- 007_exec_diag.sql
-- §6.2 Execution diagnostics. One row per fill we observe, capturing the
-- expected slippage we modelled (ISlippageModel) versus what actually filled.
-- The live engine does not USE the slippage estimate — it submits at zero
-- assumption — but logging the delta gives us a calibration loop to tune the
-- backtest model against live conditions.
--
-- Also stores a ledger of bracket pairs (BracketLinks) so the futures-side
-- emulated OCO survives a process restart: when one leg fills, reconciliation
-- looks up the sibling and cancels it, even if the in-memory link table is
-- empty after a crash.
--
-- Idempotent: every CREATE is guarded.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- ExecutionDiagnostics: one row per Fill (FK), with modelled vs observed
-- slippage in basis points and absolute price. ExpectedPrice is the price the
-- model predicted when the order was placed; ActualPrice is the volume-weighted
-- fill price. Side is +1 for BUY, -1 for SELL so a positive ObservedSlippageBps
-- always means "worse than expected".
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ExecutionDiagnostics', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExecutionDiagnostics
    (
        DiagnosticId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExecutionDiagnostics PRIMARY KEY,
        OrderId             BIGINT          NOT NULL,
        FillId              BIGINT          NULL,
        SignalId            BIGINT          NULL,
        SymbolId            INT             NOT NULL,
        Side                CHAR(4)         NOT NULL,
        OrderType           VARCHAR(24)     NOT NULL,
        ExpectedPrice       DECIMAL(38,18)  NOT NULL,
        ActualPrice         DECIMAL(38,18)  NOT NULL,
        Quantity            DECIMAL(38,18)  NOT NULL,
        ExpectedSlippageBps DECIMAL(18,6)   NOT NULL,
        ObservedSlippageBps DECIMAL(18,6)   NOT NULL,
        SpreadBps           DECIMAL(18,6)   NULL,
        ParticipationPct    DECIMAL(18,6)   NULL,
        ModelVersion        VARCHAR(16)     NOT NULL CONSTRAINT DF_ExecDiag_ModelVersion DEFAULT ('v1'),
        RecordedAt          DATETIME2(3)    NOT NULL CONSTRAINT DF_ExecDiag_RecordedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_ExecDiag_Orders   FOREIGN KEY (OrderId)  REFERENCES dbo.Orders(OrderId),
        CONSTRAINT FK_ExecDiag_Fills    FOREIGN KEY (FillId)   REFERENCES dbo.Fills(FillId),
        CONSTRAINT FK_ExecDiag_Signals  FOREIGN KEY (SignalId) REFERENCES dbo.Signals(SignalId),
        CONSTRAINT FK_ExecDiag_Symbols  FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_ExecDiag_Symbol_RecordedAt' AND object_id = OBJECT_ID(N'dbo.ExecutionDiagnostics'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ExecDiag_Symbol_RecordedAt
        ON dbo.ExecutionDiagnostics (SymbolId, RecordedAt DESC)
        INCLUDE (ObservedSlippageBps, ExpectedSlippageBps);
END;
GO

-- ---------------------------------------------------------------------------
-- BracketLinks: ledger of paired SL/TP orders for the futures-emulated OCO.
-- When one leg fills, the reconciliation/userData reactor looks up the sibling
-- and submits a CANCEL. The Reserved flag is a soft mutex preventing two
-- concurrent reactors from racing to cancel the same sibling: the cancellation
-- worker flips it to 1 transactionally before issuing the REST call.
-- One PositionId may have many historical links (one per trail update), but
-- only one row per (PositionId, Leg) where Status='ACTIVE' at any time.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.BracketLinks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BracketLinks
    (
        BracketLinkId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BracketLinks PRIMARY KEY,
        PositionId          BIGINT          NOT NULL,
        StopOrderId         BIGINT          NOT NULL,    -- FK Orders (STOP_MARKET)
        TakeProfitOrderId   BIGINT          NOT NULL,    -- FK Orders (TAKE_PROFIT_MARKET)
        StopClientOrderId   VARCHAR(36)     NOT NULL,
        TpClientOrderId     VARCHAR(36)     NOT NULL,
        AccountType         VARCHAR(8)      NOT NULL,
        SymbolId            INT             NOT NULL,
        Status              VARCHAR(16)     NOT NULL CONSTRAINT DF_BracketLinks_Status DEFAULT ('ACTIVE'),
        ReservedSibling     CHAR(2)         NULL,        -- 'SL' or 'TP' once one side starts cancelling.
        CreatedAt           DATETIME2(3)    NOT NULL CONSTRAINT DF_BracketLinks_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ResolvedAt          DATETIME2(3)    NULL,
        CONSTRAINT FK_BracketLinks_Positions FOREIGN KEY (PositionId)        REFERENCES dbo.Positions(PositionId),
        CONSTRAINT FK_BracketLinks_StopOrder FOREIGN KEY (StopOrderId)       REFERENCES dbo.Orders(OrderId),
        CONSTRAINT FK_BracketLinks_TpOrder   FOREIGN KEY (TakeProfitOrderId) REFERENCES dbo.Orders(OrderId),
        CONSTRAINT FK_BracketLinks_Symbols   FOREIGN KEY (SymbolId)          REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BracketLinks_Position_Status' AND object_id = OBJECT_ID(N'dbo.BracketLinks'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_BracketLinks_Position_Status
        ON dbo.BracketLinks (PositionId, Status)
        INCLUDE (StopOrderId, TakeProfitOrderId, StopClientOrderId, TpClientOrderId, ReservedSibling);
END;
GO

-- Lookup-by-clientOrderId is the hot path when a userData WS event arrives.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BracketLinks_StopCid' AND object_id = OBJECT_ID(N'dbo.BracketLinks'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_BracketLinks_StopCid
        ON dbo.BracketLinks (StopClientOrderId);
    CREATE NONCLUSTERED INDEX IX_BracketLinks_TpCid
        ON dbo.BracketLinks (TpClientOrderId);
END;
GO
