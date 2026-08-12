using System.Reflection;
using System.Text.Json;
using MarketPulse.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
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

    /// <summary>
    /// Two racing first-ever trades for the same user both take the implicit
    /// portfolio-creation path (see <c>PortfolioController</c>/the command handler): no
    /// <c>Portfolio</c> row exists yet, so there is no <c>RowVersion</c> to guard the
    /// insert and the loser hits <c>IX_Portfolios_UserId</c> instead, surfacing as a plain
    /// <see cref="DbUpdateException"/> wrapping a <see cref="SqlException"/> (error 2601 or
    /// 2627), not a <see cref="DbUpdateConcurrencyException"/>. Before this fix that fell
    /// through to the catch-all and became a 500; the retry-safe remedy is identical to
    /// the concurrency-token case, so it maps to the same 409.
    ///
    /// <see cref="SqlException"/> is sealed with no public constructor that lets a test
    /// set <see cref="SqlException.Number"/>, and the driver's own factory
    /// (<c>SqlException.CreateException</c>) is internal. Both <see cref="SqlError"/>'s
    /// constructor and <see cref="SqlErrorCollection.Add"/> ARE public, so the only
    /// reflection needed is to reach the collection's internal parameterless constructor
    /// and the internal <c>CreateException</c> factory — verified against the exact
    /// <c>Microsoft.Data.SqlClient</c> version this solution pins in
    /// <c>Directory.Packages.props</c> before relying on it here.
    /// </summary>
    [Fact]
    public async Task A_unique_index_violation_from_a_racing_implicit_portfolio_create_maps_to_409()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var sqlException = BuildSqlException(number: 2601);
        var dbUpdateException = new DbUpdateException("Saving changes failed.", sqlException);

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw dbUpdateException,
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(body);
        Assert.Equal(
            "concurrent-update",
            document.RootElement.GetProperty("title").GetString());
    }

    /// <summary>
    /// Builds a real <see cref="SqlException"/> carrying the given error number, since the
    /// middleware's catch filter pattern-matches on <c>SqlException.Number</c> and nothing
    /// short of a genuine instance exercises that match. <see cref="SqlErrorCollection"/>'s
    /// constructor and <see cref="SqlException"/>'s own construction path are both
    /// internal to the driver; <see cref="SqlError"/>'s constructor and
    /// <see cref="SqlErrorCollection.Add"/> are public.
    /// </summary>
    private static SqlException BuildSqlException(int number)
    {
        var errors = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection), nonPublic: true)!;

        var errorCtor = typeof(SqlError).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(int), typeof(byte), typeof(byte), typeof(string),
                typeof(string), typeof(string), typeof(int), typeof(int), typeof(Exception)
            ],
            modifiers: null)
            ?? throw new MissingMethodException("SqlError's 9-arg constructor was not found.");

        var error = (SqlError)errorCtor.Invoke(
        [
            number, (byte)0, (byte)14, "(local)",
            "Violation of UNIQUE constraint.", "", 1, 0, null
        ]);

        errors.GetType()
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(errors, [error]);

        var createException = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name == "CreateException" && m.GetParameters().Length == 2);

        return (SqlException)createException.Invoke(null, [errors, "11.0.0"])!;
    }
}
