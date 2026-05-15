SET NOCOUNT ON;

DECLARE @SymbolId INT = (SELECT SymbolId FROM dbo.Symbols WHERE Symbol = 'BTCUSDT' AND Exchange = 'BINANCE_SPOT');
IF @SymbolId IS NULL
BEGIN
    RAISERROR('BTCUSDT@BINANCE_SPOT not found in dbo.Symbols.', 16, 1);
    RETURN;
END

;WITH bars5m AS (
    SELECT
        c.SymbolId,
        DATEADD(HOUR, DATEDIFF(HOUR, 0, c.OpenTime), 0) AS HourBucket,
        c.OpenTime, c.[Open] AS [Open], c.High, c.Low, c.[Close] AS [Close], c.Volume, c.QuoteVolume,
        c.TradeCount, c.TakerBuyBase
    FROM dbo.Candles c
    WHERE c.SymbolId = @SymbolId AND c.Interval = '5m'
),
hourly AS (
    SELECT
        SymbolId,
        HourBucket AS OpenTime,
        DATEADD(SECOND, -1, DATEADD(HOUR, 1, HourBucket)) AS CloseTime,
        COUNT(*) AS BarCount,
        MAX(High) AS High,
        MIN(Low)  AS Low,
        SUM(Volume) AS Volume,
        SUM(QuoteVolume) AS QuoteVolume,
        SUM(TradeCount)  AS TradeCount,
        SUM(TakerBuyBase) AS TakerBuyBase,
        -- Open of the first 5m bar in the hour:
        (SELECT TOP 1 [Open]  FROM bars5m b2
            WHERE b2.SymbolId = bars5m.SymbolId AND b2.HourBucket = bars5m.HourBucket
            ORDER BY b2.OpenTime ASC) AS [Open],
        -- Close of the last 5m bar in the hour:
        (SELECT TOP 1 [Close] FROM bars5m b3
            WHERE b3.SymbolId = bars5m.SymbolId AND b3.HourBucket = bars5m.HourBucket
            ORDER BY b3.OpenTime DESC) AS [Close]
    FROM bars5m
    GROUP BY SymbolId, HourBucket
)
INSERT INTO dbo.Candles
    (SymbolId, Interval, OpenTime, CloseTime, [Open], High, Low, [Close],
     Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed, InsertedAt)
SELECT
    h.SymbolId, '1h', h.OpenTime, h.CloseTime, h.[Open], h.High, h.Low, h.[Close],
    h.Volume, h.QuoteVolume, h.TradeCount, h.TakerBuyBase, 1, SYSUTCDATETIME()
FROM hourly h
WHERE h.BarCount = 12     -- only complete hours
  AND NOT EXISTS (SELECT 1 FROM dbo.Candles c
                  WHERE c.SymbolId = h.SymbolId
                    AND c.Interval = '1h'
                    AND c.OpenTime = h.OpenTime);

SELECT @@ROWCOUNT AS InsertedRows;

SELECT Interval, COUNT(*) AS Bars, MIN(OpenTime) AS MinUtc, MAX(OpenTime) AS MaxUtc
FROM dbo.Candles
WHERE SymbolId = @SymbolId
GROUP BY Interval
ORDER BY Interval;
