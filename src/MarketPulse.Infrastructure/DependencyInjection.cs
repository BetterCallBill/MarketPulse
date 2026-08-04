using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Authentication;
using MarketPulse.Infrastructure.Persistence;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IWatchlistRepository, WatchlistRepository>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<PriceTickChannel>();
        services.AddHostedService<FakeTickService>();
        return services;
    }
}
