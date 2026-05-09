-- sql/migrations/010_alerts.sql
-- §11 alert journal. One row per non-deduplicated alert; consumed by the
-- WARN 6h digest and the daily 06:00 UTC digest. Schema is small but indexed
-- on the two access paths (severity+window, plain window).

IF OBJECT_ID('dbo.AlertJournal', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AlertJournal (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertJournal PRIMARY KEY,
        SentAtUtc       DATETIME2(3)  NOT NULL,
        Severity        TINYINT       NOT NULL,    -- 0=Info,1=Warn,2=Error,3=Critical
        Title           NVARCHAR(256) NOT NULL,
        Body            NVARCHAR(MAX) NOT NULL,
        Fingerprint     CHAR(64)      NOT NULL,
        Transports      NVARCHAR(128) NOT NULL,
        InstanceId      NVARCHAR(64)  NOT NULL,
        CorrelationId   UNIQUEIDENTIFIER NULL
    );

    CREATE INDEX IX_AlertJournal_SentAtUtc
        ON dbo.AlertJournal (SentAtUtc);

    CREATE INDEX IX_AlertJournal_Severity_SentAtUtc
        ON dbo.AlertJournal (Severity, SentAtUtc);
END
