using System.Diagnostics;

namespace MarketPulse.Api.Middleware;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied.ToString()
                : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // The ID finally lands somewhere searchable: on the trace and on every log line.
        Activity.Current?.SetTag("correlation.id", correlationId);

        // A list — not a Dictionary — because BeginScope's state type is preserved as-is by
        // the logging pipeline, and structured-log consumers (including FakeLogger in tests)
        // pattern-match scope state against IReadOnlyList<KeyValuePair<string, object?>>,
        // which Dictionary<TKey, TValue> does not implement.
        using var scope = logger.BeginScope(
            new List<KeyValuePair<string, object?>> { new("CorrelationId", correlationId) });

        await next(context);
    }
}
