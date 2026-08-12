using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class NotificationRepository(MarketPulseDbContext db) : INotificationRepository
{
    public async Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct) =>
        await db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public Task<Notification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

    public async Task AddAsync(Notification notification, CancellationToken ct) =>
        await db.Notifications.AddAsync(notification, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
