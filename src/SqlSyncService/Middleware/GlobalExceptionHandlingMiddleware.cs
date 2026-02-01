using System.Net;
using System.Text.Json;
using SqlSyncService.Contracts;

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

            _logger.LogError(ex, "Unhandled exception occurred while processing the request.");

            var error = new ErrorDetail
            {
                Code = ErrorCodes.InternalError,
                Message = "An unexpected error occurred.",
                Details = null,
                TraceId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            var errorResponse = new ErrorResponse { Error = error };

            var json = JsonSerializer.Serialize(errorResponse);

            context.Response.Clear();
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(json).ConfigureAwait(false);
        }
    }
}
