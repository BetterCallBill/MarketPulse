-- What GetSparklinesAsync (fixed) does: one set-based query for all 20 tickers, same
-- STATISTICS TIME/IO capture as the before script for a like-for-like comparison.
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
DECLARE @FromUtc datetimeoffset = DATEADD(MINUTE, -60, SYSDATETIMEOFFSET());
DECLARE @ToUtc   datetimeoffset = SYSDATETIMEOFFSET();
-- Named selectedTickers, not "tickers": SQL Server's default case-insensitive collation
-- makes a CTE literally named "tickers" collide with the Tickers table it selects from,
-- so "FROM Tickers" inside its own definition resolves to itself — SQL Server then reads
-- it as a recursive CTE and rejects it for lacking a UNION ALL anchor (Msg 252).
WITH selectedTickers AS (SELECT TOP (20) Code FROM Tickers ORDER BY Code),
ranged AS (
    SELECT p.Ticker, p.TimestampUtc, p.Price,
           DATEDIFF(SECOND, '2000-01-01', p.TimestampUtc) / 60 AS Bucket
    FROM PriceTicks p JOIN selectedTickers t ON p.Ticker = t.Code
    WHERE p.TimestampUtc >= @FromUtc AND p.TimestampUtc < @ToUtc
)
SELECT Ticker, Bucket, Price AS [Close]
FROM (SELECT Ticker, Bucket, Price,
             ROW_NUMBER() OVER (PARTITION BY Ticker, Bucket ORDER BY TimestampUtc DESC) AS Rn
      FROM ranged) x
WHERE Rn = 1
ORDER BY Ticker, Bucket;
