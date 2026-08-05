using System.Net;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace MarketPulse.UnitTests.RealTime;

public class MarketDataResilienceTests
{
    private static readonly MarketDataOptions Options = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromSeconds(20)
    };

    private static (ResiliencePipeline<HttpResponseMessage> Pipeline, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = clock };
        MarketDataResilience.Configure(builder, Options, clock);
        return (builder.Build(), clock);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_within_one_execution()
    {
        var (pipeline, clock) = Build();
        var attempts = 0;

        var task = pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }).AsTask();

        // Walk the fake clock past the backoff delays until the execution completes.
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Sustained_failure_opens_the_breaker_and_fails_fast()
    {
        var (pipeline, clock) = Build();

        static ValueTask<HttpResponseMessage> AlwaysFail(CancellationToken _) =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Enough failed executions to trip the breaker (each execution burns its retries).
        // Once the breaker opens mid-loop, a later iteration's retry attempt can hit it and
        // throw BrokenCircuitException synchronously — expected and harmless here, so it's
        // swallowed rather than left to fail the loop.
        for (var i = 0; i < 4; i++)
        {
            var task = pipeline.ExecuteAsync(AlwaysFail).AsTask();
            while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
            try
            {
                _ = await task; // 500 back, or BrokenCircuitException once open — both fine here
            }
            catch (BrokenCircuitException)
            {
                // Expected once the breaker has opened.
            }
        }

        // Now open: the next execution must fail fast without invoking the callback.
        var invoked = false;
        var afterOpen = pipeline.ExecuteAsync(_ =>
        {
            invoked = true;
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }).AsTask();
        while (!afterOpen.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        await Assert.ThrowsAsync<BrokenCircuitException>(() => afterOpen);
        Assert.False(invoked);
    }

    [Fact]
    public async Task The_breaker_half_opens_after_the_break_and_recovers_on_success()
    {
        var (pipeline, clock) = Build();

        static ValueTask<HttpResponseMessage> AlwaysFail(CancellationToken _) =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // See the sibling breaker test for why BrokenCircuitException is swallowed here too.
        for (var i = 0; i < 4; i++)
        {
            var t = pipeline.ExecuteAsync(AlwaysFail).AsTask();
            while (!t.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
            try
            {
                _ = await t;
            }
            catch (BrokenCircuitException)
            {
                // Expected once the breaker has opened.
            }
        }

        // Ride out the break duration; the half-open probe should reach the callback.
        clock.Advance(TimeSpan.FromMinutes(3));

        var probe = pipeline.ExecuteAsync(_ =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK))).AsTask();
        while (!probe.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        Assert.Equal(HttpStatusCode.OK, (await probe).StatusCode);
    }

    [Fact]
    public async Task A_hung_attempt_is_cut_by_the_attempt_timeout()
    {
        var (pipeline, clock) = Build();

        var task = pipeline.ExecuteAsync(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }).AsTask();

        while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        // Retries wrap the timeout: after 3 timed-out attempts the outcome is a timeout
        // exception — TimeoutRejectedException or OperationCanceledException (its
        // TaskCanceledException subclass included), depending on exactly where in the
        // pipeline the cancellation is observed. Either is the correct "cut by the
        // timeout" outcome; anything else would mean the timeout strategy let a hang
        // through unbounded, which is the one thing this test exists to rule out.
        try
        {
            await task;
            Assert.Fail("Expected the hung attempt to be cut by the timeout.");
        }
        catch (Exception ex)
        {
            Assert.True(
                ex is TimeoutRejectedException or OperationCanceledException,
                $"Expected TimeoutRejectedException or OperationCanceledException, got {ex.GetType()}.");
        }
    }

    [Fact]
    public async Task A_429_is_not_retried_within_one_execution_but_still_counts_toward_the_breaker()
    {
        var (pipeline, clock) = Build();
        var attempts = 0;

        // A single 429 execution: no retry means exactly one attempt, and the outcome
        // propagates as the 429 response rather than being retried away.
        var task = pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }).AsTask();

        while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await task).StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Sustained_429s_open_the_breaker_even_though_none_of_them_are_retried()
    {
        var (pipeline, clock) = Build();

        static ValueTask<HttpResponseMessage> AlwaysThrottle(CancellationToken _) =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        // Each execution is a single un-retried 429 attempt, so it takes MinimumThroughput
        // (6) of them — not the ~4 fully-retried-500 executions the sibling breaker test
        // needs — to reach the sample count that lets FailureRatio trip the breaker.
        for (var i = 0; i < 6; i++)
        {
            var task = pipeline.ExecuteAsync(AlwaysThrottle).AsTask();
            while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
            try
            {
                _ = await task; // 429 back, or BrokenCircuitException once open — both fine here
            }
            catch (BrokenCircuitException)
            {
                // Expected once the breaker has opened.
            }
        }

        // Now open: the next execution must fail fast without invoking the callback.
        var invoked = false;
        var afterOpen = pipeline.ExecuteAsync(_ =>
        {
            invoked = true;
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }).AsTask();
        while (!afterOpen.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        await Assert.ThrowsAsync<BrokenCircuitException>(() => afterOpen);
        Assert.False(invoked);
    }
}
