using System.Text.Json;
using MarketPulse.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.UnitTests.Api;

public class ExceptionHandlingMiddlewareTests
{
    /// <summary>
    /// PortfolioConcurrencyAnomalyTests proves EF throws <see cref="DbUpdateConcurrencyException"/>
    /// on the losing side of a raced trade; this pins down the other half of that path — the
    /// middleware maps it to the 409 the client is expected to retry on, with the ProblemDetails
    /// title ("concurrent-update") the client matches against. No HTTP host or timing needed:
    /// a bare <see cref="DefaultHttpContext"/> and a next delegate that throws is the whole unit.
    /// </summary>
    [Fact]
    public async Task A_concurrency_exception_maps_to_409_with_the_concurrent_update_title()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new DbUpdateConcurrencyException(),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(body);
        Assert.Equal(
            "concurrent-update",
            document.RootElement.GetProperty("title").GetString());
    }
}
