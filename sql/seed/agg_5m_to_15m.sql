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
        DATEADD(MINUTE,
                (DATEDIFF(MINUTE, '2000-01-01T00:00:00', c.OpenTime) / 15) * 15,
                '2000-01-01T00:00:00') AS Bucket15m,
        c.OpenTime, c.[Open] AS [Open], c.High, c.Low, c.[Close] AS [Close],
        c.Volume, c.QuoteVolume, c.TradeCount, c.TakerBuyBase
    FROM dbo.Candles c
    WHERE c.SymbolId = @SymbolId AND c.Interval = '5m'
),
quarter AS (
    SELECT
        SymbolId,
        Bucket15m AS OpenTime,
        DATEADD(SECOND, -1, DATEADD(MINUTE, 15, Bucket15m)) AS CloseTime,
        COUNT(*) AS BarCount,
        MAX(High) AS High,
        MIN(Low)  AS Low,
        SUM(Volume) AS Volume,
        SUM(QuoteVolume) AS QuoteVolume,
        SUM(TradeCount) AS TradeCount,
        SUM(TakerBuyBase) AS TakerBuyBase,
        (SELECT TOP 1 [Open]  FROM bars5m b2
            WHERE b2.SymbolId = bars5m.SymbolId AND b2.Bucket15m = bars5m.Bucket15m
            ORDER BY b2.OpenTime ASC) AS [Open],
        (SELECT TOP 1 [Close] FROM bars5m b3
            WHERE b3.SymbolId = bars5m.SymbolId AND b3.Bucket15m = bars5m.Bucket15m
            ORDER BY b3.OpenTime DESC) AS [Close]
    FROM bars5m
    GROUP BY SymbolId, Bucket15m
)
INSERT INTO dbo.Candles
    (SymbolId, Interval, OpenTime, CloseTime, [Open], High, Low, [Close],
     Volume, QuoteVolume, TradeCount, TakerBuyBase, IsClosed, InsertedAt)
SELECT
    q.SymbolId, '15m', q.OpenTime, q.CloseTime, q.[Open], q.High, q.Low, q.[Close],
    q.Volume, q.QuoteVolume, q.TradeCount, q.TakerBuyBase, 1, SYSUTCDATETIME()
FROM quarter q
WHERE q.BarCount = 3
  AND NOT EXISTS (SELECT 1 FROM dbo.Candles c
                  WHERE c.SymbolId = q.SymbolId
                    AND c.Interval = '15m'
                    AND c.OpenTime = q.OpenTime);

SELECT @@ROWCOUNT AS InsertedRows;
