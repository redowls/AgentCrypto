-- =============================================================================
-- 008_news.sql
-- §5.4.1 — NewsSentiment stores one row per (source, headline-hash) +
-- one row per per-asset NDJSON verdict produced by Claude. The table is
-- queried by the strategy layer to read recent average sentiment per asset
-- (see ISignal enrichment) and by the daily digest job in S11.
--
-- AiJournals stores the markdown produced by the Sunday journal job (§5.4.4)
-- so the report is recoverable without filesystem access (e.g. when the bot
-- runs in a container with ephemeral storage).
--
-- Idempotent: every CREATE is guarded.
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------
-- NewsSentiment — Claude verdicts per news item × asset (NDJSON unrolled).
-- An item that mentions multiple assets generates multiple rows, all sharing
-- HeadlineHash so the analyzer can dedupe ingestion of the same headline
-- arriving from different feeds.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.NewsSentiment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.NewsSentiment
    (
        NewsSentimentId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_NewsSentiment PRIMARY KEY,
        ItemTimestamp   DATETIME2(3)  NOT NULL,
        Source          VARCHAR(64)   NOT NULL,
        HeadlineHash    CHAR(64)      NOT NULL,   -- SHA-256(source|headline) — lower-case hex
        Headline        NVARCHAR(512) NOT NULL,
        Asset           VARCHAR(16)   NOT NULL,   -- ticker or 'GLOBAL'
        Sentiment       DECIMAL(5,4)  NOT NULL,   -- -1.0000 .. +1.0000
        Confidence      DECIMAL(5,4)  NOT NULL,   --  0.0000 .. +1.0000
        Horizon         VARCHAR(16)   NOT NULL,   -- INTRADAY|SWING|LONG
        Rationale       NVARCHAR(256) NULL,
        Actionable      BIT           NOT NULL,
        AiInteractionId BIGINT        NULL,       -- pointer back to dbo.AiInteractions
        CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_NewsSentiment_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT UQ_NewsSentiment_HashAsset UNIQUE (HeadlineHash, Asset),
        CONSTRAINT FK_NewsSentiment_Ai FOREIGN KEY (AiInteractionId)
            REFERENCES dbo.AiInteractions(AiInteractionId),
        CONSTRAINT CK_NewsSentiment_Sentiment CHECK (Sentiment BETWEEN -1 AND 1),
        CONSTRAINT CK_NewsSentiment_Confidence CHECK (Confidence BETWEEN 0 AND 1)
    );
END;
GO

-- "Recent sentiment for asset X" lookup — strategy layer asks for last N hours.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_NewsSentiment_Asset_Ts' AND object_id = OBJECT_ID(N'dbo.NewsSentiment'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_NewsSentiment_Asset_Ts
        ON dbo.NewsSentiment (Asset, ItemTimestamp DESC)
        INCLUDE (Sentiment, Confidence, Actionable, Horizon);
END;
GO

-- ---------------------------------------------------------------------------
-- AiJournals — weekly Claude post-trade reports (Sunday 06:00 UTC job).
-- One row per ISO week. The markdown is also written to /journals/YYYY-WW.md
-- on disk for the operator; the DB copy is the source of truth.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.AiJournals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiJournals
    (
        AiJournalId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiJournals PRIMARY KEY,
        IsoYear         INT           NOT NULL,
        IsoWeek         INT           NOT NULL,
        PeriodStartUtc  DATETIME2(3)  NOT NULL,
        PeriodEndUtc    DATETIME2(3)  NOT NULL,
        TradesAnalyzed  INT           NOT NULL,
        Markdown        NVARCHAR(MAX) NOT NULL,
        AiInteractionId BIGINT        NULL,
        CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_AiJournals_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT UQ_AiJournals_Year_Week UNIQUE (IsoYear, IsoWeek),
        CONSTRAINT FK_AiJournals_Ai FOREIGN KEY (AiInteractionId)
            REFERENCES dbo.AiInteractions(AiInteractionId)
    );
END;
GO
