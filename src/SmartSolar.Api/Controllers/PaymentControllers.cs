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

/// <summary>Review of a technician cash receipt: the receipt <c>revision</c> you reviewed; <c>reason</c> required to reject; optional <c>note</c> to accept.</summary>
public sealed record CashReceiptReviewRequest(int? Revision, string? Reason, string? Note);

/// <summary>
/// Manager/Admin payment desk (2026-11-18): every payment attempt (contract, combined checkout, package, after-sales quotation), paged, and the
/// cash confirmation of one CASH attempt by its id (the route for after-sales cash; a contract attempt follows the contract rules).
/// </summary>
[ApiController]
[Route("api/manager/payments")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class StaffPaymentDeskController(PaymentService payments, SmartSolar.Modules.CustomerContracts.ContractDocumentHandlers documents,
    SmartSolar.Modules.AfterSales.AfterSalesEvidenceUrlHandlers afterSalesUrls) : OperationResultControllerBase
{
    /// <summary>
    /// Technician cash receipts (2026-11-27), one queue for installation installments and after-sales quotations:
    /// GET ?status=SUBMITTED|ACCEPTED|REJECTED|WITHDRAWN&amp;kind=INSTALLMENT|SERVICE_QUOTATION&amp;page=1&amp;pageSize=20 — newest first.
    /// </summary>
    [HttpGet("cash-receipts")]
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<StaffCashReceiptView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> CashReceipts([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page ?? 1, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        var s = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (s is not null && !CashReceiptStatuses.All.Contains(s)) return Invalid("status", $"'status' must be one of {string.Join(", ", CashReceiptStatuses.All)}.");
        var k = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToUpperInvariant();
        if (k is not null && !CashReceiptKinds.All.Contains(k)) return Invalid("kind", $"'kind' must be one of {string.Join(", ", CashReceiptKinds.All)}.");
        return Ok(ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<StaffCashReceiptView>>.Success(
            await payments.PageCashReceiptsAsync(s, paging.Value!, cancellationToken, k), HttpContext.GetTraceId()));
    }

    /// <summary>One receipt with its contract / installment / customer / technician context and every receipt sent for the same payment.</summary>
    [HttpGet("cash-receipts/{receiptId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<StaffCashReceiptDetail>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> CashReceipt(Guid receiptId, CancellationToken cancellationToken)
        => Respond(await payments.GetCashReceiptForStaffAsync(receiptId, cancellationToken));

    /// <summary>Short-lived URL of the technician's photo (no re-upload needed). Audited; <c>Cache-Control: no-store</c>.</summary>
    [HttpGet("cash-receipts/{receiptId:guid}/evidence/url")]
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> CashReceiptEvidenceUrl(Guid receiptId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        var receipt = await payments.FindCashReceiptAsync(receiptId, cancellationToken);
        if (receipt is null) return Respond(SmartSolar.Modules.Common.Results.OperationResult<bool>.Fail(CashReceiptErrorCodes.NotFound, "The cash receipt was not found."));
        return receipt.Kind == CashReceiptKinds.ServiceQuotation
            ? Respond(await afterSalesUrls.CashReceiptAsync(receipt, parsed, cancellationToken))
            : Respond(await documents.CashReceiptEvidenceUrlAsync(user, User.IsInRole(RoleCodes.Admin) ? RoleCodes.Admin : RoleCodes.Manager, receipt, parsed, cancellationToken));
    }

    /// <summary>
    /// Accepts the receipt at the revision you reviewed: the payment becomes PAID with its number, photo and collection time and the installment is settled
    /// (30 % deposit or 70 % balance). 409 PAYMENT_CASH_RECEIPT_NOT_REVIEWABLE / _STALE, PAYMENT_ALREADY_PAID, PAYMENT_CASH_RECEIPT_DUPLICATE.
    /// </summary>
    [HttpPost("cash-receipts/{receiptId:guid}/accept")]
    [ProducesResponseType(typeof(ApiResponse<PaymentAttemptView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> AcceptCashReceipt(Guid receiptId, [FromBody] CashReceiptReviewRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await payments.AcceptCashReceiptAsync(user, receiptId, revision, request.Note, cancellationToken));
    }

    /// <summary>Asks the technician for a new receipt (<c>reason</c> required, shown to the technician). The payment keeps waiting for cash confirmation.</summary>
    [HttpPost("cash-receipts/{receiptId:guid}/reject")]
    [ProducesResponseType(typeof(ApiResponse<CashReceiptView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> RejectCashReceipt(Guid receiptId, [FromBody] CashReceiptReviewRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await payments.RejectCashReceiptAsync(user, receiptId, revision, request.Reason, cancellationToken));
    }

    /// <summary>GET ?method=CASH|ONLINE&amp;status=PENDING&amp;from=&amp;to=&amp;page=1&amp;pageSize=20 — newest first; `from`/`to` are ISO instants on the creation time.</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? method, [FromQuery] string? status, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page ?? 1, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        var m = string.IsNullOrWhiteSpace(method) ? null : method.Trim().ToUpperInvariant();
        if (m is not (null or PaymentMethods.Cash or PaymentMethods.Online)) return Invalid("method", "'method' must be CASH or ONLINE.");
        var s = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (s is not null && !PaymentStatuses.All.Contains(s)) return Invalid("status", $"'status' must be one of {string.Join(", ", PaymentStatuses.All)}.");
        if (from is { } f && to is { } t && t <= f) return Invalid("to", "'to' must be after 'from'.");
        return Ok(ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<StaffPaymentAttemptView>>.Success(
            await payments.PageForStaffAsync(new PaymentAttemptFilter(m, s, from?.ToUniversalTime(), to?.ToUniversalTime()), paging.Value!, cancellationToken),
            HttpContext.GetTraceId()));
    }

    /// <summary>Records the cash received for one CASH attempt: exact amount due, unique receipt, VERIFIED PAYMENT_EVIDENCE of the caller.</summary>
    [HttpPost("{attemptId:guid}/cash/confirm")]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> ConfirmCash(Guid attemptId, [FromBody] CashConfirmationRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Amount is not { } amount) return Invalid("amount", "'amount' is required.");
        if (request.EvidenceFileId is not { } evidence) return Invalid("evidenceFileId", "'evidenceFileId' is required.");
        return Respond(await payments.ConfirmCashAttemptAsync(user, attemptId, new CashConfirmation(amount, request.ReceiptReference, evidence, request.Note), cancellationToken));
    }
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
