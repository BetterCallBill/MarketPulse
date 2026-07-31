using FluentValidation;
using MarketPulse.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message);
        }
        catch (ValidationException ex)
        {
            var code = ex.Errors.FirstOrDefault()?.ErrorCode ?? "validation-failed";
            var detail = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            await WriteAsync(context, StatusCodes.Status400BadRequest, code, detail);
        }
        catch (Exception ex)
        {
            var correlationId = CorrelationId(context);
            logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                "internal-error", "An unexpected error occurred.");
        }
    }

    private static string CorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? "unknown";

    private static async Task WriteAsync(
        HttpContext context, int status, string errorCode, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = errorCode,
            Type = $"https://marketpulse.local/errors/{errorCode}",
            Detail = detail
        };
        problem.Extensions["correlationId"] = CorrelationId(context);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json");
    }
}
