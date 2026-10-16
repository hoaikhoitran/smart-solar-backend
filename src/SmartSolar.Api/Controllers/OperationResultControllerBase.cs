using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Envelope, identity and error-code → HTTP mapping for controllers whose use cases return
/// <see cref="OperationResult{T}"/> (Installation Flow, shared files, knowledge base). Each controller
/// declares its own <see cref="RequireFeatureAttribute"/>.
/// </summary>
public abstract class OperationResultControllerBase : ControllerBase
{
    protected Guid? CurrentUserId()
        => Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    protected bool IsInRole(string role) => User.IsInRole(role);

    protected IActionResult UnauthorizedFailure()
        => Unauthorized(ApiResponse<object>.Failure(new ApiError(AuthErrorCodes.Unauthorized, "Authentication is required."), HttpContext.GetTraceId()));

    protected IActionResult Respond<T>(OperationResult<T> result, int successStatus = StatusCodes.Status200OK)
        => result.Succeeded
            ? StatusCode(successStatus, ApiResponse<T>.Success(result.Value!, HttpContext.GetTraceId()))
            : StatusCode(StatusFor(result.ErrorCode!), ApiResponse<object>.Failure(new ApiError(result.ErrorCode!, result.Message!, result.Details), HttpContext.GetTraceId()));

    protected IActionResult Invalid(string field, string message)
        => BadRequest(ApiResponse<object>.Failure(new ApiError(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] }), HttpContext.GetTraceId()));

    /// <summary>
    /// Mapping rules: *_NOT_FOUND → 404; validation, type and key errors → 400; ownership → 403;
    /// payment provider outages → 502/503; everything else (state, staleness, duplicates, conflicts) → 409.
    /// </summary>
    public static int StatusFor(string code)
    {
        if (code.EndsWith("NOT_FOUND", StringComparison.Ordinal)) return StatusCodes.Status404NotFound;
        if (code is CommonErrorCodes.ValidationFailed || code.Contains("VALIDATION", StringComparison.Ordinal) || code.StartsWith("FILE_INVALID", StringComparison.Ordinal)
            || code is "FILE_TYPE_NOT_ALLOWED" or "FILE_TOO_LARGE" or "FILE_BUCKET_NOT_ALLOWED" or "FILE_WRONG_PURPOSE" or "QUOTATION_INVALID_LINE"
                or "RAG_MESSAGE_TOO_LONG" or "PAYMENT_AMOUNT_NOT_SUPPORTED")
            return StatusCodes.Status400BadRequest;
        if (code is CommonErrorCodes.Forbidden or "FILE_NOT_OWNED" or "INSTALLATION_NOT_LEADER") return StatusCodes.Status403Forbidden;
        if (code is CommonErrorCodes.FeaturePendingMigration or "PAYMENT_PROVIDER_NOT_CONFIGURED" or "RAG_SERVICE_UNAVAILABLE" or "TRAVEL_PROVIDER_UNAVAILABLE" or "FILE_STORAGE_UNAVAILABLE")
            return StatusCodes.Status503ServiceUnavailable;
        if (code is "PAYMENT_PROVIDER_ERROR") return StatusCodes.Status502BadGateway;
        if (code is "RAG_QUOTA_EXCEEDED" or "RAG_TOO_MANY_CONCURRENT_REQUESTS" or "PAYMENT_TOO_MANY_ATTEMPTS") return StatusCodes.Status429TooManyRequests;
        return StatusCodes.Status409Conflict;
    }
}
