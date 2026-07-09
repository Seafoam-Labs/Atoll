using System.Diagnostics;
using System.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api;

public class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService
) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title) = exception switch
        {
            ArgumentException or ArgumentOutOfRangeException or NotSupportedException
                => (StatusCodes.Status400BadRequest, exception.Message),

            FileNotFoundException or KeyNotFoundException or NotImplementedException
                => (StatusCodes.Status404NotFound, "Resource not found"),

            UnauthorizedAccessException
                => (StatusCodes.Status401Unauthorized, "Unauthorized access"),

            SecurityException
                => (StatusCodes.Status403Forbidden, "Access forbidden"),

            OperationCanceledException or TaskCanceledException
                => (StatusCodes.Status408RequestTimeout, "Request canceled or timed out"),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred. Please try again later.")
        };

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Instance = httpContext.Request.Path,
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }
}