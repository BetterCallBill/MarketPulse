using System.Data.Common;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Counts connections handed to the Dapper read path. Each reader method opens exactly one
/// connection and runs exactly one query on it, so connections == queries — the pin that
/// keeps ADR-006's N+1 from silently returning.
/// </summary>
public sealed class CountingSqlConnectionFactory(ISqlConnectionFactory inner) : ISqlConnectionFactory
{
    private int _opened;

    public int Opened => Volatile.Read(ref _opened);

    public void Reset() => Volatile.Write(ref _opened, 0);

    public Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _opened);
        return inner.OpenAsync(ct);
    }
}
