using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Authentication;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Persistence;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        string connectionString,
        IConfiguration configuration)
    {
        services.AddPersistence(connectionString);
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<PriceTickChannel>();

        // Typed client + resilience pipeline for the real tick producer. Registering this
        // is inert on its own — nothing resolves YahooQuoteClient until Task 4 adds the
        // hosted service that consumes it — so it is safe to register unconditionally,
        // alongside the fake below.
        services.AddHttpClient<YahooQuoteClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        })
        .AddResilienceHandler("market-data", (builder, context) =>
        {
            var options = context.ServiceProvider
                .GetRequiredService<IOptions<MarketDataOptions>>().Value;
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>().CreateLogger("MarketPulse.MarketData.Resilience");
            MarketDataResilience.Configure(builder, options, TimeProvider.System, logger);
        });

        // Exactly one tick producer. Read raw here because hosted-service registration
        // happens before options validation runs; a bad value still fails startup via
        // ValidateOnStart, and the composition tests pin both sides.
        var source = configuration[$"{MarketDataOptions.SectionName}:Source"];

        // TimeProvider.System unless a test already registered a fake — the poll timer
        // needs one either way.
        services.TryAddSingleton(TimeProvider.System);

        if (string.Equals(source, MarketDataOptions.YahooSource, StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<YahooPriceFeedService>();
        }
        else
        {
            services.AddHostedService<FakeTickService>();
        }

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
