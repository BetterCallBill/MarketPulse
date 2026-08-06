-- What GetSparklinesHandler v1 does: N single-ticker candle queries, replayed here as a
-- T-SQL CURSOR loop over one connection. That loop is a harness stand-in, not a faithful
-- replay: the real v1 code opened a separate connection and issued a separate round trip
-- per ticker, which this single in-server batch cannot reproduce. The cursor's own
-- OPEN/FETCH bookkeeping is measured harness overhead the real loop never paid — see the
-- "harness divergence" note in docs/sql/README.md before reading the STATISTICS IO/TIME
-- output below as if it were the real loop's server-side cost.
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
DECLARE @FromUtc datetimeoffset = DATEADD(MINUTE, -60, SYSDATETIMEOFFSET());
DECLARE @ToUtc   datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @Ticker  nvarchar(8);
DECLARE tickers CURSOR FOR SELECT TOP (20) Code FROM Tickers ORDER BY Code;
OPEN tickers;
FETCH NEXT FROM tickers INTO @Ticker;
WHILE @@FETCH_STATUS = 0
BEGIN
    WITH ranged AS (
        SELECT TimestampUtc, Price,
               DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / 60 AS Bucket
        FROM PriceTicks
        WHERE Ticker = @Ticker AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
    )
    SELECT Bucket, MAX(CASE WHEN Rn = 1 THEN Price END) AS [Close]
    FROM (SELECT Bucket, Price,
                 ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc DESC) AS Rn
          FROM ranged) x
    WHERE Rn = 1
    GROUP BY Bucket ORDER BY Bucket;
    FETCH NEXT FROM tickers INTO @Ticker;
END;
CLOSE tickers; DEALLOCATE tickers;
