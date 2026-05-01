-- =============================================================================
-- 005_regimes.sql
-- Persists every regime classification produced by the rule-based classifier.
-- One row per (SymbolId, Interval, AsOf, Source) — Source distinguishes the
-- rule-based output from the AI-confirmed output added in S9 (CLAUDE_CONFIRMED).
-- Inputs holds the snapshot fields that drove the verdict (JSON), so a reviewer
-- can reproduce the call from a row alone.
-- Idempotent: every CREATE is guarded by an existence check.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.Regimes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Regimes
    (
        RegimeId    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Regimes PRIMARY KEY,
        SymbolId    INT            NOT NULL,
        [Interval]  VARCHAR(8)     NOT NULL,
        AsOf        DATETIME2(3)   NOT NULL,
        Regime      VARCHAR(16)    NOT NULL,
        Confidence  DECIMAL(5,4)   NOT NULL,
        Source      VARCHAR(24)    NOT NULL CONSTRAINT DF_Regimes_Source DEFAULT ('RULE'),
        Inputs      NVARCHAR(MAX)  NULL,
        CreatedAt   DATETIME2(3)   NOT NULL CONSTRAINT DF_Regimes_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Regimes_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId),
        CONSTRAINT UQ_Regimes_Sym_Tf_AsOf_Src UNIQUE (SymbolId, [Interval], AsOf, Source)
    );
END;
GO

-- Lookup index for "what was the latest regime for (symbol, interval)?" — the
-- Signal Engine asks this on every bar close, so the index supports a top-1
-- DESC-on-AsOf scan with a covering INCLUDE list.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_Regimes_Sym_Tf_AsOf' AND object_id = OBJECT_ID(N'dbo.Regimes'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Regimes_Sym_Tf_AsOf
        ON dbo.Regimes (SymbolId, [Interval], AsOf DESC)
        INCLUDE (Regime, Confidence, Source);
END;
GO
