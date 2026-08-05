using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class PortfolioRepository(MarketPulseDbContext db) : IPortfolioRepository
{
    public Task<Portfolio?> GetForUserAsync(Guid userId, CancellationToken ct) =>
        db.Portfolios.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task AddAsync(Portfolio portfolio, CancellationToken ct) =>
        await db.Portfolios.AddAsync(portfolio, ct);

    public async Task AddTransactionAsync(Transaction transaction, CancellationToken ct) =>
        await db.Transactions.AddAsync(transaction, ct);

    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        Guid portfolioId, int skip, int take, CancellationToken ct) =>
        await db.Transactions
            .Where(t => t.PortfolioId == portfolioId)
            .OrderByDescending(t => t.OccurredUtc)
            .ThenByDescending(t => t.RecordedUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
