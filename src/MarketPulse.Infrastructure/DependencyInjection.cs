using MarketPulse.Application.Abstractions;
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
        services.AddSingleton<PriceTickChannel>();
        services.AddHostedService<FakeTickService>();
        return services;
    }
}
