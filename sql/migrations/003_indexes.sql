-- =============================================================================
-- 003_indexes.sql
-- Non-clustered indexes from §2.4. Idempotent: each guarded by a sys.indexes check.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ---------------------------------------------------------------------------
-- Candles
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Candles_SymbolInterval_Time' AND object_id = OBJECT_ID(N'dbo.Candles'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Candles_SymbolInterval_Time
        ON dbo.Candles (SymbolId, [Interval], OpenTime DESC)
        INCLUDE ([Open], [High], [Low], [Close], Volume)
        ON ps_CandleMonth(OpenTime);
END;
GO

-- ---------------------------------------------------------------------------
-- Signals
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Signals_Sym_Time' AND object_id = OBJECT_ID(N'dbo.Signals'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Signals_Sym_Time
        ON dbo.Signals (SymbolId, BarOpenTime DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Signals_Status_CT' AND object_id = OBJECT_ID(N'dbo.Signals'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Signals_Status_CT
        ON dbo.Signals (Status, CreatedAt DESC);
END;
GO

-- ---------------------------------------------------------------------------
-- Orders
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Orders_Symbol_Status' AND object_id = OBJECT_ID(N'dbo.Orders'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Orders_Symbol_Status
        ON dbo.Orders (SymbolId, Status);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Orders_Submitted' AND object_id = OBJECT_ID(N'dbo.Orders'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Orders_Submitted
        ON dbo.Orders (SubmittedAt DESC);
END;
GO

-- ---------------------------------------------------------------------------
-- Positions
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Positions_Status' AND object_id = OBJECT_ID(N'dbo.Positions'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Positions_Status
        ON dbo.Positions (Status, SymbolId);
END;
GO

-- ---------------------------------------------------------------------------
-- TradeHistory
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_TH_Strategy_Exit' AND object_id = OBJECT_ID(N'dbo.TradeHistory'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_TH_Strategy_Exit
        ON dbo.TradeHistory (Strategy, ExitTime DESC);
END;
GO

-- ---------------------------------------------------------------------------
-- AccountSnapshots
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Acct_Time' AND object_id = OBJECT_ID(N'dbo.AccountSnapshots'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Acct_Time
        ON dbo.AccountSnapshots (SnapshotTime DESC);
END;
GO

-- ---------------------------------------------------------------------------
-- RiskEvents
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Risk_Type_Time' AND object_id = OBJECT_ID(N'dbo.RiskEvents'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Risk_Type_Time
        ON dbo.RiskEvents (EventType, EventTime DESC);
END;
GO

-- ---------------------------------------------------------------------------
-- AiInteractions
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Ai_Hash' AND object_id = OBJECT_ID(N'dbo.AiInteractions'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Ai_Hash
        ON dbo.AiInteractions (InputHash);
END;
GO
