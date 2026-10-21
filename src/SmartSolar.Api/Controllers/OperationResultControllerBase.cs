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

    /// <summary>
    /// For responses that carry presigned URLs (bearer credentials): forbid any cache (browser, proxy, CDN) from keeping them.
    /// Set before the body is written, so failures carry it too.
    /// </summary>
    protected void NoStore()
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }

    /// <summary>"inline" (default when omitted) or "attachment", case-insensitive; null for anything else.</summary>
    protected static SmartSolar.Modules.Files.StorageUrlDisposition? ParseDisposition(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "inline" => SmartSolar.Modules.Files.StorageUrlDisposition.Inline,
            "attachment" => SmartSolar.Modules.Files.StorageUrlDisposition.Attachment,
            _ => null,
        };

    protected IActionResult Invalid(string field, string message)
        => BadRequest(ApiResponse<object>.Failure(new ApiError(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] }), HttpContext.GetTraceId()));

    /// <summary>
    /// Mapping rules: *_NOT_FOUND → 404; validation, type and key errors → 400; ownership → 403; site location / zone coverage missing → 422;
    /// payment provider and storage outages → 502/503; a stored object deleted from storage → 410; everything else (state, staleness, duplicates, conflicts) → 409.
    /// </summary>
    public static int StatusFor(string code)
    {
        if (code.EndsWith("NOT_FOUND", StringComparison.Ordinal)) return StatusCodes.Status404NotFound;
        if (code is CommonErrorCodes.ValidationFailed || code.Contains("VALIDATION", StringComparison.Ordinal) || code.StartsWith("FILE_INVALID", StringComparison.Ordinal)
            || code is "FILE_TYPE_NOT_ALLOWED" or "FILE_TOO_LARGE" or "FILE_BUCKET_NOT_ALLOWED" or "FILE_WRONG_PURPOSE" or "QUOTATION_INVALID_LINE"
                or "RAG_MESSAGE_TOO_LONG" or "PAYMENT_AMOUNT_NOT_SUPPORTED" or "AFTER_SALES_REASON_REQUIRED" or "CONTRACT_WARRANTY_REQUIRED"
            || code.StartsWith("SYSTEM_SETTING_", StringComparison.Ordinal))
            return StatusCodes.Status400BadRequest;
        if (code is CommonErrorCodes.Forbidden or "FILE_NOT_OWNED") return StatusCodes.Status403Forbidden;
        // Same codes and status as survey booking: the site cannot be served until its location / zone data is fixed.
        if (code is "SITE_LOCATION_REQUIRED" or "NO_ZONE_COVERAGE" or "ZONE_NOT_SERVICEABLE"
            or "ADMINISTRATIVE_LOCATION_UNKNOWN" or "ADMINISTRATIVE_LOCATION_SUPERSEDED" or "ADMINISTRATIVE_LOCATION_OUT_OF_SERVICE_AREA")
            return StatusCodes.Status422UnprocessableEntity;
        if (code is CommonErrorCodes.FeaturePendingMigration or "PAYMENT_PROVIDER_NOT_CONFIGURED" or "RAG_SERVICE_UNAVAILABLE" or "FILE_STORAGE_UNAVAILABLE"
            or "STORAGE_NOT_CONFIGURED" or "STORAGE_UNAVAILABLE")
            return StatusCodes.Status503ServiceUnavailable;
        // The stored object of a verified file was deleted from storage: gone for good.
        if (code is "FILE_OBJECT_MISSING" or "INSTALLATION_EVIDENCE_OBJECT_MISSING") return StatusCodes.Status410Gone;
        if (code is "PAYMENT_PROVIDER_ERROR") return StatusCodes.Status502BadGateway;
        if (code is "RAG_QUOTA_EXCEEDED" or "RAG_TOO_MANY_CONCURRENT_REQUESTS" or "PAYMENT_TOO_MANY_ATTEMPTS") return StatusCodes.Status429TooManyRequests;
        return StatusCodes.Status409Conflict;
    }
}
