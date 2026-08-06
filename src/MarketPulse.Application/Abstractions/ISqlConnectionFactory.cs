using System.Data.Common;

namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Hands out open connections for the Dapper read path. An abstraction so tests can count
/// or intercept connections (the N+1 pin in this slice does exactly that). EF Core's
/// DbContext does not go through this.
/// </summary>
public interface ISqlConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken ct);
}
