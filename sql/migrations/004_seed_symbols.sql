-- =============================================================================
-- 004_seed_symbols.sql
-- Seed the four MVP symbols on both BINANCE_SPOT and BINANCE_UMFUT.
-- Filter values (TickSize / StepSize / MinNotional) are placeholders sufficient
-- for dev; the live values come from /api/v3/exchangeInfo on first sync (S3).
-- Idempotent via NOT EXISTS guard on (Exchange, Symbol).
-- =============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @seeds TABLE
(
    Exchange    VARCHAR(16),
    Symbol      VARCHAR(32),
    BaseAsset   VARCHAR(16),
    QuoteAsset  VARCHAR(16),
    TickSize    DECIMAL(38,18),
    StepSize    DECIMAL(38,18),
    MinNotional DECIMAL(38,18)
);

INSERT INTO @seeds (Exchange, Symbol, BaseAsset, QuoteAsset, TickSize, StepSize, MinNotional)
VALUES
    -- Spot
    ('BINANCE_SPOT',  'BTCUSDT', 'BTC', 'USDT', 0.01,    0.00001,  10),
    ('BINANCE_SPOT',  'ETHUSDT', 'ETH', 'USDT', 0.01,    0.0001,   10),
    ('BINANCE_SPOT',  'SOLUSDT', 'SOL', 'USDT', 0.01,    0.001,    10),
    ('BINANCE_SPOT',  'BNBUSDT', 'BNB', 'USDT', 0.01,    0.001,    10),
    -- USDⓂ Futures
    ('BINANCE_UMFUT', 'BTCUSDT', 'BTC', 'USDT', 0.10,    0.001,     5),
    ('BINANCE_UMFUT', 'ETHUSDT', 'ETH', 'USDT', 0.01,    0.001,     5),
    ('BINANCE_UMFUT', 'SOLUSDT', 'SOL', 'USDT', 0.001,   1,         5),
    ('BINANCE_UMFUT', 'BNBUSDT', 'BNB', 'USDT', 0.01,    0.01,      5);

INSERT INTO dbo.Symbols (Exchange, Symbol, BaseAsset, QuoteAsset, TickSize, StepSize, MinNotional, IsActive)
SELECT s.Exchange, s.Symbol, s.BaseAsset, s.QuoteAsset, s.TickSize, s.StepSize, s.MinNotional, 1
FROM   @seeds s
WHERE  NOT EXISTS (
    SELECT 1 FROM dbo.Symbols x
    WHERE x.Exchange = s.Exchange AND x.Symbol = s.Symbol
);
GO
