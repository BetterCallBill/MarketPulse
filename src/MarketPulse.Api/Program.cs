using System.Text;
using System.Threading.RateLimiting;
using MarketPulse.Api;
using MarketPulse.Api.Authentication;
using MarketPulse.Api.Hubs;
using MarketPulse.Api.Messaging;
using MarketPulse.Api.Middleware;
using MarketPulse.Api.RealTime;
using MarketPulse.Application;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

// Options pattern with startup validation: a missing or too-short signing key fails the
// process at boot rather than at the first login attempt.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The Jwt configuration section is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // `sub` is mapped to ClaimTypes.NameIdentifier, which is what ICurrentUser reads.
        // Set explicitly rather than relying on the framework default.
        options.MapInboundClaims = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),

            // No grace period. A 15-minute token must stop working at 15 minutes, or the
            // silent-refresh test is measuring the default five-minute skew instead.
            ClockSkew = TimeSpan.Zero
        };

        // The token lives in an httpOnly cookie, not an Authorization header. This also
        // covers SignalR: cookies ride both the negotiate request and the websocket
        // handshake, so the hub needs no query-string token.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(AuthCookies.Access, out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },

            OnChallenge = async context =>
            {
                // Suppress the default empty-bodied 401 so the response matches the
                // ProblemDetails contract every other error in this API follows.
                context.HandleResponse();

                if (context.Response.HasStarted)
                {
                    return;
                }

                var correlationId =
                    context.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
                    ?? "unknown";

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "unauthenticated",
                    Type = "https://marketpulse.local/errors/unauthenticated",
                    Detail = "Authentication is required."
                };
                problem.Extensions["correlationId"] = correlationId;

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    problem, options: null, contentType: "application/problem+json");
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Read AuthOptions from DI per request rather than a value captured once at startup:
    // integration tests override LoginRequestsPerMinute through WebApplicationFactory
    // configuration, which lands after this file's top-level statements would have already
    // run, so a snapshot taken there would never see the override.
    options.AddPolicy("auth", context =>
    {
        var currentLimit = context.RequestServices
            .GetRequiredService<IOptionsMonitor<AuthOptions>>()
            .CurrentValue.LoginRequestsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = currentLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddHostedService<TickBroadcaster>();
builder.Services.AddSingleton<ITickSink, SignalRTickSink>();
builder.Services.AddHostedService<AlertTriggeredConsumer>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddMessaging();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<PriceHub>("/hubs/prices");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

public partial class Program;
