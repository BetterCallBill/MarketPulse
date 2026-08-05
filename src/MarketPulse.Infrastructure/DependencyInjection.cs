using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Authentication;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Persistence;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Everything both hosts need: the shared database and the repositories over it. The
    /// worker takes this; the API takes this plus the tick source and the auth services.
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services, string connectionString)
    {
        // Both calls configure the same connection. Each of AddDbContextFactory and
        // AddDbContext contributes its own IDbContextOptionsConfiguration<TContext> entry
        // to an IEnumerable EF Core aggregates when it lazily builds DbContextOptions — it
        // is additive, not deduplicated by TryAdd, so both entries exist regardless of call
        // order. IDbContextFactory<MarketPulseDbContext> is a singleton and resolves that
        // collection from the root container; if either entry were Scoped (AddDbContext's
        // default), the aggregate IEnumerable becomes unresolvable from the root and every
        // first call to CreateDbContext throws "Cannot resolve scoped service ... from root
        // provider". Passing optionsLifetime: Singleton to AddDbContext keeps every entry in
        // that collection singleton while leaving MarketPulseDbContext itself scoped (its
        // default contextLifetime) — which the request-scoped repositories still need. See
        // IdempotencyStore's doc comment for why the factory exists at all.
        services.AddDbContextFactory<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
        services.AddDbContext<MarketPulseDbContext>(
            o => o.UseSqlServer(connectionString), optionsLifetime: ServiceLifetime.Singleton);
        services.AddScoped<IWatchlistRepository, WatchlistRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IPortfolioRepository, PortfolioRepository>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        return services;
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddPersistence(connectionString);
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<PriceTickChannel>();
        services.AddHostedService<FakeTickService>();
        return services;
    }

    /// <summary>
    /// The broker plumbing shared by every host that talks to RabbitMQ. The API registers
    /// this to publish ticks; Task 7's worker host reuses it unchanged to consume them.
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<ITickSink, RabbitMqTickSink>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        return services;
    }
}
