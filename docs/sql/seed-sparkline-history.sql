-- Populate a retention-window-scale PriceTicks: 20 seeded tickers × 7 days × 1/minute
-- ≈ 201,600 rows. Idempotent-ish: IGNORE_DUP_KEY absorbs replays on re-run.
SET NOCOUNT ON;
;WITH n AS (
    SELECT TOP (10080) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
),
t AS (SELECT TOP (20) Code FROM Tickers ORDER BY Code)
INSERT INTO PriceTicks (Ticker, TimestampUtc, Price)
SELECT t.Code,
       DATEADD(MINUTE, -n.i, SYSDATETIMEOFFSET()),
       50 + (ABS(CHECKSUM(t.Code)) % 100) + (n.i % 97) / 100.0
FROM t CROSS JOIN n;
DECLARE @RowCount bigint = (SELECT COUNT(*) FROM PriceTicks);
PRINT CONCAT('PriceTicks rows: ', @RowCount);
