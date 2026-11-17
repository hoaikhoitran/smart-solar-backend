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

/// <summary>
/// Customer: pay 100% of a signed contract ONLINE through PayOS (one payable link at a time) or choose CASH (a Manager/Admin then records the
/// receipt). Only one method can be in progress; choosing cash is not a payment.
/// </summary>
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

    /// <summary>Cancels the online link in progress (checked with PayOS first) or withdraws the cash choice.</summary>
    [HttpPost("cancel")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> Cancel(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.CancelCurrentAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// Chooses CASH: the payment state becomes AWAITING_CASH_CONFIRMATION (the contract stays SIGNED, nothing is paid). Refused while an online
    /// link is open (cancel it first). Idempotent.
    /// </summary>
    [HttpPost("cash")]
    [ProducesResponseType(typeof(ApiResponse<PaymentAttemptView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> SelectCash(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.SelectCashAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Manager/Admin: payment attempts and history of a contract, and the confirmation of a cash payment. Sales has no payment access.</summary>
[ApiController]
[Route("api/contracts/{contractId:guid}/payments")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class StaffPaymentsController(PaymentService payments) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => Respond(await payments.GetForStaffAsync(contractId, cancellationToken));

    /// <summary>
    /// Records the cash the customer paid: exactly the contract total, a unique receipt reference and a VERIFIED PAYMENT_EVIDENCE file uploaded by
    /// the caller. The contract becomes PAID; a repeated confirmation is refused (409 PAYMENT_ALREADY_PAID).
    /// </summary>
    [HttpPost("cash/confirm")]
    [ProducesResponseType(typeof(ApiResponse<PaymentAttemptView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> ConfirmCash(Guid contractId, [FromBody] CashConfirmationRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Amount is not { } amount) return Invalid("amount", "'amount' is required.");
        if (request.EvidenceFileId is not { } evidence) return Invalid("evidenceFileId", "'evidenceFileId' is required.");
        return Respond(await payments.ConfirmCashAsync(user, contractId, new CashConfirmation(amount, request.ReceiptReference, evidence, request.Note), cancellationToken));
    }
}

/// <summary>Cash confirmation by a Manager/Admin.</summary>
public sealed record CashConfirmationRequest(decimal? Amount, string? ReceiptReference, Guid? EvidenceFileId, string? Note);

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
