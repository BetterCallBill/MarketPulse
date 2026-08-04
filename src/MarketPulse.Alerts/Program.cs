using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// AddPersistence, not AddInfrastructure: the worker shares the database but must not start
// a second FakeTickService writing ticks nobody reads.
builder.Services.AddPersistence(connectionString);
builder.Services.AddMessaging();

builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHostedService<PriceConsumer>();
builder.Services.AddHostedService<OutboxDispatcher>();

var host = builder.Build();
await host.RunAsync();
