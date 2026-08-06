using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

builder.Services.AddOptions<OtelOptions>()
    .Bind(builder.Configuration.GetSection(OtelOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var otlpEndpoint = builder.Configuration[$"{OtelOptions.SectionName}:OtlpEndpoint"]
    ?? "http://localhost:4317";

builder.Services.AddSerilog(loggerConfiguration => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .WriteTo.OpenTelemetry(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "marketpulse-alerts" };
    }));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("marketpulse-alerts"))
    .WithTracing(t => t
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation()
        .AddSource(MarketPulse.Application.Telemetry.Telemetry.MessagingSourceName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(m => m
        .AddRuntimeInstrumentation()
        .AddMeter(MarketPulse.Application.Telemetry.Telemetry.MeterName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

// AddPersistence, not AddInfrastructure: the worker shares the database but must not start
// a second FakeTickService writing ticks nobody reads.
builder.Services.AddPersistence(connectionString);
builder.Services.AddMessaging();

builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHostedService<PriceConsumer>();
builder.Services.AddHostedService<OutboxDispatcher>();

var host = builder.Build();
await host.RunAsync();
