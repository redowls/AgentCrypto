-- =============================================================================
-- 002_partition_function.sql
-- pf_CandleMonth + ps_CandleMonth, then the partitioned dbo.Candles table and
-- a heap dbo.Candles_Stage table used by SqlBulkCopy upserts.
--
-- The partition function uses RANGE RIGHT on month boundaries from 2026-01-01
-- through 2027-01-01 (13 boundaries -> 14 partitions). Adding new months later
-- is a SPLIT operation done by the monthly maintenance job (§2.5).
--
-- All partitions go to [PRIMARY] for now; in Phase 3 they get split out to
-- per-quarter filegroups and the older ones move to a columnstore archive
-- table via SWITCH PARTITION.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- Partition function (idempotent)
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_CandleMonth')
BEGIN
    CREATE PARTITION FUNCTION pf_CandleMonth (DATETIME2(3))
        AS RANGE RIGHT FOR VALUES (
            '2026-01-01T00:00:00.000','2026-02-01T00:00:00.000','2026-03-01T00:00:00.000',
            '2026-04-01T00:00:00.000','2026-05-01T00:00:00.000','2026-06-01T00:00:00.000',
            '2026-07-01T00:00:00.000','2026-08-01T00:00:00.000','2026-09-01T00:00:00.000',
            '2026-10-01T00:00:00.000','2026-11-01T00:00:00.000','2026-12-01T00:00:00.000',
            '2027-01-01T00:00:00.000');
END;
GO

-- ---------------------------------------------------------------------------
-- Partition scheme (idempotent) — all partitions to [PRIMARY] for Phase 1.
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_CandleMonth')
BEGIN
    CREATE PARTITION SCHEME ps_CandleMonth
        AS PARTITION pf_CandleMonth ALL TO ([PRIMARY]);
END;
GO

-- ---------------------------------------------------------------------------
-- Candles (heavy time-series, partitioned)
-- Clustered PK leads with the partition column so partition elimination works.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Candles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Candles
    (
        SymbolId        INT            NOT NULL,
        [Interval]      VARCHAR(8)     NOT NULL,
        OpenTime        DATETIME2(3)   NOT NULL,
        CloseTime       DATETIME2(3)   NOT NULL,
        [Open]          DECIMAL(38,18) NOT NULL,
        [High]          DECIMAL(38,18) NOT NULL,
        [Low]           DECIMAL(38,18) NOT NULL,
        [Close]         DECIMAL(38,18) NOT NULL,
        Volume          DECIMAL(38,18) NOT NULL,
        QuoteVolume     DECIMAL(38,18) NOT NULL,
        TradeCount      INT            NOT NULL,
        TakerBuyBase    DECIMAL(38,18) NOT NULL,
        IsClosed        BIT            NOT NULL,
        InsertedAt      DATETIME2(3)   NOT NULL CONSTRAINT DF_Candles_InsertedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Candles PRIMARY KEY CLUSTERED
            (OpenTime, SymbolId, [Interval])
            ON ps_CandleMonth(OpenTime),
        CONSTRAINT FK_Candles_Symbols FOREIGN KEY (SymbolId) REFERENCES dbo.Symbols(SymbolId)
    );
END;
GO

-- ---------------------------------------------------------------------------
-- Candles_Stage (heap, used by SqlBulkCopy then drained via MERGE).
-- Lives on [PRIMARY] only, no partitioning, no FKs (loader-controlled).
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Candles_Stage', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Candles_Stage
    (
        SymbolId        INT            NOT NULL,
        [Interval]      VARCHAR(8)     NOT NULL,
        OpenTime        DATETIME2(3)   NOT NULL,
        CloseTime       DATETIME2(3)   NOT NULL,
        [Open]          DECIMAL(38,18) NOT NULL,
        [High]          DECIMAL(38,18) NOT NULL,
        [Low]           DECIMAL(38,18) NOT NULL,
        [Close]         DECIMAL(38,18) NOT NULL,
        Volume          DECIMAL(38,18) NOT NULL,
        QuoteVolume     DECIMAL(38,18) NOT NULL,
        TradeCount      INT            NOT NULL,
        TakerBuyBase    DECIMAL(38,18) NOT NULL,
        IsClosed        BIT            NOT NULL
    );
END;
GO
