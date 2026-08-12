using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IWatchlistRepository
{
    Task<Watchlist?> GetForUserAsync(Guid userId, CancellationToken ct);
    Task AddAsync(Watchlist watchlist, CancellationToken ct);
    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
