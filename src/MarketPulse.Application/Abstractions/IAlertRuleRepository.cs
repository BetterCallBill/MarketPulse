using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IAlertRuleRepository
{
    Task<IReadOnlyList<AlertRule>> GetForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Scoped by user on purpose: a rule belonging to someone else must be indistinguishable
    /// from one that does not exist.
    /// </summary>
    Task<AlertRule?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct);

    Task<int> CountForUserAsync(Guid userId, CancellationToken ct);

    Task<bool> ActiveDuplicateExistsAsync(
        Guid userId, string ticker, AlertDirection direction, decimal threshold,
        CancellationToken ct);

    /// <summary>The worker's per-tick query. Not scoped by user — it evaluates everyone's.</summary>
    Task<IReadOnlyList<AlertRule>> GetActiveForTickerAsync(string ticker, CancellationToken ct);

    Task AddAsync(AlertRule rule, CancellationToken ct);
    void Remove(AlertRule rule);
    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
