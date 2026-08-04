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
        services.AddDbContext<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IWatchlistRepository, WatchlistRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IOutbox, Outbox>();
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
        return services;
    }
}
