-- =============================================================================
-- 006_correlations.sql
-- §8.3 — Rolling 30-day return-correlation matrix and greedy-threshold cluster
-- assignments. The matrix is rebuilt nightly by the CorrelationRefreshJob; the
-- risk gate reads only the most recent AsOf row. Each (AsOf, SymbolIdA,
-- SymbolIdB) is unique; the matrix is stored as the symmetric upper-triangle
-- (SymbolIdA <= SymbolIdB) plus the diagonal.
--
-- ClusterAssignments holds the greedy single-link partition produced from
-- the same matrix at the same AsOf — one row per symbol, with the cluster
-- index it was placed in. The risk gate uses this table to answer
-- "is a position already open in the same cluster on the same side?".
--
-- All times DATETIME2(3) UTC. Idempotent: every CREATE is guarded.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- Correlations: pairwise return correlations, 30d default lookback.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Correlations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Correlations
    (
        CorrelationId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Correlations PRIMARY KEY,
        AsOf            DATETIME2(3)  NOT NULL,
        SymbolIdA       INT           NOT NULL,
        SymbolIdB       INT           NOT NULL,
        LookbackDays    INT           NOT NULL,
        Correlation     DECIMAL(7,6)  NOT NULL,
        SampleCount     INT           NOT NULL,
        CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_Correlations_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Correlations_SymbolA FOREIGN KEY (SymbolIdA) REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT FK_Correlations_SymbolB FOREIGN KEY (SymbolIdB) REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT UQ_Correlations_Pair UNIQUE (AsOf, SymbolIdA, SymbolIdB),
        -- Triangle invariant — keeps the table half the size and simplifies lookups.
        CONSTRAINT CK_Correlations_Triangle CHECK (SymbolIdA <= SymbolIdB)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Correlations_SymbolA_AsOf' AND object_id = OBJECT_ID(N'dbo.Correlations'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Correlations_SymbolA_AsOf
        ON dbo.Correlations (SymbolIdA, AsOf DESC)
        INCLUDE (SymbolIdB, Correlation, SampleCount);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Correlations_SymbolB_AsOf' AND object_id = OBJECT_ID(N'dbo.Correlations'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Correlations_SymbolB_AsOf
        ON dbo.Correlations (SymbolIdB, AsOf DESC)
        INCLUDE (SymbolIdA, Correlation, SampleCount);
END;
GO

-- ---------------------------------------------------------------------------
-- CorrelationClusters: the greedy partition produced from the matrix above.
-- One row per (AsOf, SymbolId). A cluster of size 1 is still recorded so the
-- risk gate can answer "is this symbol clustered with anything?" cheaply.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.CorrelationClusters', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrelationClusters
    (
        AsOf            DATETIME2(3)  NOT NULL,
        SymbolId        INT           NOT NULL,
        Cluster         INT           NOT NULL,
        Threshold       DECIMAL(7,6)  NOT NULL,
        CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_CorrelationClusters_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_CorrelationClusters PRIMARY KEY (AsOf, SymbolId),
        CONSTRAINT FK_CorrelationClusters_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CorrelationClusters_AsOf_Cluster' AND object_id = OBJECT_ID(N'dbo.CorrelationClusters'))
BEGIN
    -- "Find every symbol in the same cluster as me at the latest AsOf" lookup.
    CREATE NONCLUSTERED INDEX IX_CorrelationClusters_AsOf_Cluster
        ON dbo.CorrelationClusters (AsOf DESC, Cluster, SymbolId);
END;
GO
