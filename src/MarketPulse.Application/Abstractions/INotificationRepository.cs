using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface INotificationRepository
{
    Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct);

    Task<Notification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct);
    Task AddAsync(Notification notification, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
