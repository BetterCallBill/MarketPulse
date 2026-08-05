using MarketPulse.Api.Filters;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MarketPulse.UnitTests.Api;

/// <summary>
/// Covers the post-<c>next()</c> release decision <see cref="IdempotencyFilter"/> makes:
/// a failure of the action itself (surfaced on <c>ActionExecutedContext.Exception</c>)
/// releases the claim so the same key may be retried safely, but a failure that happens
/// only after the action already succeeded — storing/serializing the response for replay
/// — must NOT release it, or an honest retry would re-execute an already-committed action.
/// </summary>
public class IdempotencyFilterTests
{
    private readonly IIdempotencyStore _store = Substitute.For<IIdempotencyStore>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Guid _userId = Guid.NewGuid();

    public IdempotencyFilterTests()
    {
        _user.UserId.Returns(_userId);
        _store.FindAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyKey?)null);
        _store.TryClaimAsync(Arg.Any<IdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    /// <summary>
    /// The action's SaveChanges already committed (that's what "action succeeded" means
    /// here); only recording the replay response failed. Removing the claim now would let
    /// an honest same-key retry re-execute the action and produce a silent duplicate — the
    /// exact bug this fix closes. RED against the pre-fix filter: the old code's blanket
    /// <c>catch { RemoveAsync(...); throw; }</c> around the whole post-<c>next()</c> block
    /// released the claim here too.
    /// </summary>
    [Fact]
    public async Task When_the_action_succeeds_and_completion_throws_the_claim_is_not_removed()
    {
        _store.CompleteAsync(
                Arg.Any<IdempotencyKey>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var logger = new CapturingLogger<IdempotencyFilter>();
        var filter = new IdempotencyFilter(_store, _user, logger);
        var (executingContext, next) = BuildContext(
            SuccessResult(StatusCodes.Status201Created));

        // The filter must not let the completion failure escape and lose the action's
        // own (already-correct) response.
        await filter.OnActionExecutionAsync(executingContext, next);

        await _store.DidNotReceive().RemoveAsync(Arg.Any<IdempotencyKey>(), Arg.Any<CancellationToken>());
        await _store.Received(1).CompleteAsync(
            Arg.Any<IdempotencyKey>(), 201, Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task When_the_action_throws_the_claim_is_removed_and_not_completed()
    {
        var (executingContext, next) = BuildContext(exception: new InvalidOperationException("boom"));
        var filter = new IdempotencyFilter(_store, _user, NullLogger<IdempotencyFilter>.Instance);

        await filter.OnActionExecutionAsync(executingContext, next);

        await _store.Received(1).RemoveAsync(Arg.Any<IdempotencyKey>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteAsync(
            Arg.Any<IdempotencyKey>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static ObjectResult SuccessResult(int statusCode) =>
        new(new { Id = Guid.NewGuid() }) { StatusCode = statusCode };

    private (ActionExecutingContext Executing, ActionExecutionDelegate Next) BuildContext(
        ObjectResult? result = null, Exception? exception = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new JsonOptions()));

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers[IdempotencyFilter.HeaderName] = "a-fresh-idempotency-key";

        var actionDescriptor = new ActionDescriptor
        {
            AttributeRouteInfo = new AttributeRouteInfo { Template = "api/v1/portfolio/transactions" }
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor);

        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        var executedContext = new ActionExecutedContext(
            actionContext, new List<IFilterMetadata>(), controller: new object())
        {
            Result = result,
            Exception = exception,
            ExceptionHandled = false
        };

        ActionExecutionDelegate next = () => Task.FromResult(executedContext);
        return (executingContext, next);
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
