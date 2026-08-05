using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IPortfolioRepository
{
    /// <summary>Holdings load with the aggregate (owned collection). Null when the user
    /// has never traded — creation is the caller's job, on first transaction.</summary>
    Task<Portfolio?> GetForUserAsync(Guid userId, CancellationToken ct);

    Task AddAsync(Portfolio portfolio, CancellationToken ct);

    /// <summary>Persisted beside the aggregate change in the same unit of work: the trade
    /// and its effect on the holding commit together or not at all.</summary>
    Task AddTransactionAsync(Transaction transaction, CancellationToken ct);

    /// <summary>Newest first. Reads page the table directly — the aggregate does not hold
    /// its unbounded history (ADR-004).</summary>
    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        Guid portfolioId, int skip, int take, CancellationToken ct);

    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
