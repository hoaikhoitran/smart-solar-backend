using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Refuses a request body before model binding reads it: 411 when the length is not declared (chunked upload) and 413 when the declared
/// <c>Content-Length</c> exceeds <paramref name="maxBytes"/>. Used on anonymous endpoints whose real payload is tiny, so an
/// unauthenticated caller cannot make the server buffer and parse megabytes. <see cref="RequestSizeLimitAttribute"/> stays on the action
/// as the server-level backstop (Kestrel aborts a body that is longer than declared).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BoundedRequestBodyAttribute(int maxBytes) : Attribute, IResourceFilter
{
    public const string LengthRequiredCode = "REQUEST_LENGTH_REQUIRED";
    public const string TooLargeCode = "REQUEST_BODY_TOO_LARGE";

    public int MaxBytes { get; } = maxBytes;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var length = context.HttpContext.Request.ContentLength;
        if (length is null)
            context.Result = Failure(context, StatusCodes.Status411LengthRequired, LengthRequiredCode, "A Content-Length header is required.");
        else if (length > MaxBytes)
            context.Result = Failure(context, StatusCodes.Status413PayloadTooLarge, TooLargeCode, $"The request body must be at most {MaxBytes} bytes.");
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    private static ObjectResult Failure(ResourceExecutingContext context, int status, string code, string message)
        => new(ApiResponse<object>.Failure(new ApiError(code, message), context.HttpContext.GetTraceId())) { StatusCode = status };
}
