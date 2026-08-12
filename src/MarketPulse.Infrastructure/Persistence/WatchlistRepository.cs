using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class WatchlistRepository(MarketPulseDbContext db) : IWatchlistRepository
{
    public Task<Watchlist?> GetForUserAsync(Guid userId, CancellationToken ct) =>
        db.Watchlists.FirstOrDefaultAsync(w => w.UserId == userId, ct);

    public async Task AddAsync(Watchlist watchlist, CancellationToken ct) =>
        await db.Watchlists.AddAsync(watchlist, ct);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
