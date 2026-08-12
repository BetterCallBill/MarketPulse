using System.Reflection;
using MarketPulse.Api.Middleware;
using MarketPulse.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MarketPulse.UnitTests.Api;

public class CsrfMiddlewareTests
{
    /// <summary>
    /// CsrfMiddleware exempts each hub's whole path family via StartsWithSegments, which
    /// includes long-polling's client-to-server send endpoint — safe only because the hub
    /// has nothing a client can invoke. This enforces that invariant by reflection rather
    /// than trusting the doc comment: a hub added to <see cref="CsrfMiddleware.ExemptHubs"/>
    /// with any public instance method of its own would be exempting a real attack surface.
    /// </summary>
    [Fact]
    public void Every_csrf_exempt_hub_declares_no_client_invokable_methods()
    {
        foreach (var (path, hubType) in CsrfMiddleware.ExemptHubs)
        {
            var ownPublicMethods = hubType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.True(
                ownPublicMethods.Length == 0,
                $"{hubType.Name} (exempt at {path}) declares public method(s) " +
                $"{string.Join(", ", ownPublicMethods.Select(m => m.Name))}, which a CSRF-" +
                "exempt path would let an unauthenticated-origin request invoke.");
        }
    }

    [Fact]
    public async Task A_rejected_mutation_is_logged_with_its_method_and_path()
    {
        var logger = new CapturingLogger<CsrfMiddleware>();
        var middleware = new CsrfMiddleware(_ => Task.CompletedTask, logger);
        var context = Request("POST", "/api/v1/watchlist/items");

        await Assert.ThrowsAsync<CsrfValidationException>(
            () => middleware.InvokeAsync(context));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("POST", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/api/v1/watchlist/items", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The nonce is the secret the check is built on. A log line that echoes either half of
    /// the double-submit pair hands it to anyone who can read the log.
    /// </summary>
    [Fact]
    public async Task The_rejection_log_does_not_echo_the_submitted_token()
    {
        var logger = new CapturingLogger<CsrfMiddleware>();
        var middleware = new CsrfMiddleware(_ => Task.CompletedTask, logger);
        var context = Request("POST", "/api/v1/watchlist/items");
        context.Request.Headers[CsrfMiddleware.HeaderName] = "the-attackers-guess";

        await Assert.ThrowsAsync<CsrfValidationException>(
            () => middleware.InvokeAsync(context));

        Assert.DoesNotContain(
            "the-attackers-guess", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A security log nobody reads is worse than none. Requests that pass must stay silent,
    /// or the warnings that matter drown.
    /// </summary>
    [Fact]
    public async Task A_safe_request_is_not_logged()
    {
        var logger = new CapturingLogger<CsrfMiddleware>();
        var middleware = new CsrfMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(Request("GET", "/api/v1/watchlist"));

        Assert.Empty(logger.Entries);
    }

    private static DefaultHttpContext Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
