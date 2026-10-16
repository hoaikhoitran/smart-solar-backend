using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>Customer: pay 100% of a signed contract through PayOS (one payable link at a time).</summary>
[ApiController]
[Route("api/customers/me/contracts/{contractId:guid}/payments")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerPaymentsController(PaymentService payments) : OperationResultControllerBase
{
    /// <summary>Creates the payment link, or returns the current payable one (idempotent; never two links).</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> Create(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.CreateOrGetAttemptAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Current payment state. Call this after the PayOS return URL; the return URL itself never marks a payment as paid.</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.GetForCustomerAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("cancel")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> Cancel(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.CancelCurrentAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Staff: payment attempts and history of a contract.</summary>
[ApiController]
[Route("api/contracts/{contractId:guid}/payments")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class StaffPaymentsController(PaymentService payments) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => Respond(await payments.GetForStaffAsync(contractId, cancellationToken));
}

/// <summary>
/// PayOS webhook. Anonymous by design: authenticity comes from the HMAC signature (not the caller IP),
/// deliveries are deduplicated by payload hash (replays are harmless), and no per-user rate limit
/// applies so legitimate provider retries are never blocked. Only a generous per-IP abuse guard
/// (<c>RateLimiting:PayOsWebhook</c>, 600/min) and a body size cap apply.
/// </summary>
[ApiController]
[Route("api/payments/payos/webhook")]
[AllowAnonymous]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class PayOsWebhookController(PaymentService payments) : OperationResultControllerBase
{
    public const int MaxBodyBytes = 64 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitingExtensions.PayOsWebhookPolicy)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (read > MaxBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var result = await payments.HandleWebhookAsync(new string(buffer, 0, read), cancellationToken);
        return result.Accepted
            ? Ok(ApiResponse<object>.Success(new { success = true, outcome = result.Outcome }, HttpContext.GetTraceId()))
            : BadRequest(ApiResponse<object>.Failure(new ApiError(PaymentErrorCodes.InvalidSignature, "Invalid webhook signature."), HttpContext.GetTraceId()));
    }
}
