using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class AlertRuleRepository(MarketPulseDbContext db) : IAlertRuleRepository
{
    public async Task<IReadOnlyList<AlertRule>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.AlertRules
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);

    public Task<AlertRule?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

    public Task<int> CountForUserAsync(Guid userId, CancellationToken ct) =>
        db.AlertRules.CountAsync(r => r.UserId == userId, ct);

    public Task<bool> ActiveDuplicateExistsAsync(
        Guid userId, string ticker, AlertDirection direction, decimal threshold,
        CancellationToken ct) =>
        db.AlertRules.AnyAsync(
            r => r.UserId == userId
              && r.Ticker == ticker
              && r.Direction == direction
              && r.Threshold == threshold
              && r.Status == AlertRuleStatus.Active,
            ct);

    public async Task<IReadOnlyList<AlertRule>> GetActiveForTickerAsync(
        string ticker, CancellationToken ct) =>
        await db.AlertRules
            .Where(r => r.Ticker == ticker && r.Status == AlertRuleStatus.Active)
            .ToListAsync(ct);

    public async Task AddAsync(AlertRule rule, CancellationToken ct) =>
        await db.AlertRules.AddAsync(rule, ct);

    public void Remove(AlertRule rule) => db.AlertRules.Remove(rule);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
