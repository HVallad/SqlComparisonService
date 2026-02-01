using System.Net;
using System.Text.Json;
using SqlSyncService.Contracts;
using SqlSyncService.Services;

namespace SqlSyncService.Middleware;

public sealed class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

    public GlobalExceptionHandlingMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.LogError(ex, "Unhandled exception occurred after the response started.");
                throw;
            }

            var (statusCode, errorDetail) = MapExceptionToError(context, ex);

            var errorResponse = new ErrorResponse { Error = errorDetail };
            var json = JsonSerializer.Serialize(errorResponse);

            context.Response.Clear();
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(json).ConfigureAwait(false);
        }
    }

    private (HttpStatusCode StatusCode, ErrorDetail Error) MapExceptionToError(HttpContext context, Exception ex)
    {
        switch (ex)
        {
            case SubscriptionNotFoundException notFound:
                _logger.LogWarning(ex, "Handled subscription not found: {Message}", ex.Message);
                return (HttpStatusCode.NotFound, new ErrorDetail
                {
                    Code = ErrorCodes.NotFound,
                    Message = notFound.Message,
                    Details = null,
                    Field = null,
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                });

            case SubscriptionConflictException conflict:
                _logger.LogWarning(ex, "Handled subscription conflict: {Message}", ex.Message);
                return (HttpStatusCode.Conflict, new ErrorDetail
                {
                    Code = ErrorCodes.Conflict,
                    Message = conflict.Message,
                    Details = null,
                    Field = string.IsNullOrWhiteSpace(conflict.Field) ? null : conflict.Field,
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                });

            default:
                _logger.LogError(ex, "Unhandled exception occurred while processing the request.");
                return (HttpStatusCode.InternalServerError, new ErrorDetail
                {
                    Code = ErrorCodes.InternalError,
                    Message = "An unexpected error occurred.",
                    Details = null,
                    Field = null,
                    TraceId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                });
        }
    }
}
