using System.Text.Json;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Middlewares;

/// <summary>
/// Converts unhandled exceptions into the standard envelope. The exception is
/// logged with the traceId; its message never reaches the client.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var traceId = context.GetTraceId();

            _logger.LogError(ex, "Unhandled exception. TraceId: {TraceId}", traceId);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var envelope = ApiResponse<object>.Failure(
                new ApiError(
                    AuthErrorCodes.InternalError,
                    "An unexpected error occurred. Please try again later."),
                traceId);

            await context.Response.WriteAsync(JsonSerializer.Serialize(
                envelope,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
}
