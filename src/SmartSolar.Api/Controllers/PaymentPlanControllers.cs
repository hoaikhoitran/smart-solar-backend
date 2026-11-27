using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// 30/70 plan (2026-11-19), technician on site: open / read / cancel the payment of an installment (deposit = 1, balance = 2) and upload the photographed
/// pages of the paper contract signed after the deposit. Only the technician assigned to the on-site sale (the request's surveying technician); anyone
/// else gets 404. The technician never marks money as received: CASH waits for a Manager/Admin confirmation.
/// </summary>
[ApiController]
[Route("api/technician/contracts/{contractId:guid}")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianContractPaymentPlanController(InstallmentPaymentHandlers installments, PaperSignatureHandlers signatures, ContractDocumentHandlers documents)
    : OperationResultControllerBase
{
    /// <summary>
    /// Sends the photo of the physical receipt for the installment's open CASH payment (after the technician took the cash). The installment stays DUE
    /// and the payment AWAITING_CASH_CONFIRMATION (<c>currentCashReceipt.status</c> = SUBMITTED) until a Manager/Admin accepts it; after a rejection send
    /// a new one. 400 VALIDATION_FAILED (missing photo / number), 409 PAYMENT_CASH_RECEIPT_* / PAYMENT_CASH_NOT_SELECTED, FILE_* for the photo.
    /// </summary>
    [HttpPost("installments/{sequence:int}/payment/cash-receipt")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> SubmitCashReceipt(Guid contractId, int sequence, [FromBody] CashReceiptRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await installments.SubmitCashReceiptAsync(user, contractId, sequence, new SmartSolar.Modules.Payments.CashReceiptInput(request.PaymentAttemptId ?? Guid.Empty,
                request.ReceiptNumber, request.EvidenceFileId ?? Guid.Empty, request.CollectedAt, request.Note), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Short-lived URL of the photo of one of this installment's receipts (<c>disposition</c> inline | attachment). Audited; <c>Cache-Control: no-store</c>.</summary>
    [HttpGet("installments/{sequence:int}/payment/cash-receipts/{receiptId:guid}/evidence/url")]
    [ProducesResponseType(typeof(ApiResponse<AttachedFileUrlView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> CashReceiptEvidenceUrl(Guid contractId, int sequence, Guid receiptId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        var receipt = await installments.FindCashReceiptForTechnicianAsync(user, contractId, sequence, receiptId, cancellationToken);
        if (!receipt.Succeeded) return Respond(receipt);
        return Respond(await documents.CashReceiptEvidenceUrlAsync(user, RoleCodes.Technician, receipt.Value!, parsed, cancellationToken));
    }

    /// <summary>ONLINE returns the PayOS <c>qrCode</c> / <c>checkoutUrl</c> to show on the device; CASH records a pending cash choice. Same method again → same payment.</summary>
    [HttpPost("installments/{sequence:int}/payment")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> StartPayment(Guid contractId, int sequence, [FromBody] InstallmentPaymentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.StartAsync(InstallmentPayer.Technician, user, contractId, sequence, request.Method, cancellationToken))
            : UnauthorizedFailure();

    [HttpGet("installments/{sequence:int}/payment")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> GetPayment(Guid contractId, int sequence, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.GetAsync(InstallmentPayer.Technician, user, contractId, sequence, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("installments/{sequence:int}/payment/cancel")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> CancelPayment(Guid contractId, int sequence, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.CancelAsync(InstallmentPayer.Technician, user, contractId, sequence, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Starts the signed-page upload (expected page count). Needs the verified deposit (409 CONTRACT_DEPOSIT_REQUIRED before).</summary>
    [HttpPost("signature-set")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> StartSignatureSet(Guid contractId, [FromBody] StartSignatureSetRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ExpectedPageCount is not { } pages) return Invalid("expectedPageCount", "'expectedPageCount' is required.");
        return Respond(await signatures.StartAsync(SignatureActor.Technician, user, contractId, new StartSignatureSetInput(pages, request.SignedOn, request.Note), cancellationToken));
    }

    /// <summary>Uploads or replaces (before finalization) page <c>pageNumber</c>: a VERIFIED <c>SIGNED_CONTRACT_PAGE</c> file the technician registered.</summary>
    [HttpPut("signature-set/pages/{pageNumber:int}")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> PutPage(Guid contractId, int pageNumber, [FromBody] SignedPageRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await signatures.PutPageAsync(SignatureActor.Technician, user, contractId, pageNumber, request.FileId ?? Guid.Empty, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Locks the pages (all of 1..N present and intact) → contract SIGNED (ready for installation). No Manager review.</summary>
    [HttpPost("signature-set/finalize")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Finalize(Guid contractId, [FromBody] FinalizeSignatureSetRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await signatures.FinalizeAsync(SignatureActor.Technician, user, contractId, request?.ExpectedRevision, cancellationToken))
            : UnauthorizedFailure();
}

/// <summary>
/// Contract directory and 30/70 reads for Sales, Technician, Manager and Admin (read-all, 2026-11-19): paged list with filters / search, signed pages
/// (short-lived signed URLs, audited), installment payment state. Manager/Admin may also support the signed-page upload.
/// </summary>
[ApiController]
[Route("api/contracts")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Technician},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ContractDirectoryController(ContractDirectoryHandlers directory, PaperSignatureHandlers signatures, InstallmentPaymentHandlers installments,
    ContractDocumentHandlers documents, ContractHandlers contracts) : OperationResultControllerBase
{
    /// <summary>
    /// Every contract, newest first, paged (<c>page</c> default 1, <c>pageSize</c> default 20, max 100). Filters: <c>customerId</c>, <c>status</c>,
    /// <c>paymentPlan</c> (DEPOSIT_30_BALANCE_70 | LEGACY), <c>surveyRequestId</c>, <c>propertySiteId</c>, <c>quotationId</c>, <c>projectId</c>,
    /// <c>overdue</c>; <c>search</c> = contract number, customer name, phone or e-mail.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ContractDirectoryItem>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] Guid? customerId, [FromQuery] string? status,
        [FromQuery] string? paymentPlan, [FromQuery] Guid? surveyRequestId, [FromQuery] Guid? propertySiteId, [FromQuery] Guid? quotationId, [FromQuery] Guid? projectId,
        [FromQuery] string? search, [FromQuery] bool? overdue, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var paging = PageRequest.Parse(page ?? 1, pageSize ?? PageRequest.DefaultPageSize);
        if (!paging.Succeeded) return Respond(paging);
        var result = await directory.PageAsync(new ContractListFilter(customerId, Normalize(status), Normalize(paymentPlan), surveyRequestId, propertySiteId, quotationId,
            projectId, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), overdue), paging.Value!, cancellationToken);
        if (result.Succeeded)
            await documents.AuditAsync(null, user, CurrentRole(), ContractAccessActions.List,
                $"customer={customerId} status={status} plan={paymentPlan} site={propertySiteId} project={projectId} search={(search is null ? "" : "yes")} page={page ?? 1}",
                cancellationToken);
        return Respond(result);
    }

    /// <summary>Signed-page uploads of the contract (current and superseded), with each page's file id, SHA-256 and uploader.</summary>
    [HttpGet("{contractId:guid}/signature-sets")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SignatureSetView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SignatureSets(Guid contractId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var contract = await contracts.GetForStaffAsync(user, true, contractId, cancellationToken);
        if (!contract.Succeeded) return Respond(contract);
        return Ok(ApiResponse<IReadOnlyList<SignatureSetView>>.Success(await signatures.ListAsync(contractId, cancellationToken), HttpContext.GetTraceId()));
    }

    /// <summary>Short-lived signed GET URL of one photographed page (<c>disposition</c> inline | attachment). Audited; sent with <c>Cache-Control: no-store</c>.</summary>
    [HttpGet("{contractId:guid}/signed-pages/{fileId:guid}/url")]
    [ProducesResponseType(typeof(ApiResponse<AttachedFileUrlView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SignedPageUrl(Guid contractId, Guid fileId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.SignedPageUrlAsync(ContractViewer.Staff, user, CurrentRole(), contractId, fileId, parsed, cancellationToken));
    }

    /// <summary>Payment state and attempts of one installment (read-only for staff).</summary>
    [HttpGet("{contractId:guid}/installments/{sequence:int}/payment")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> InstallmentPayment(Guid contractId, int sequence, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.GetAsync(InstallmentPayer.Staff, user, contractId, sequence, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Manager/Admin support: start the signed-page upload on behalf of the technician (same rules).</summary>
    [HttpPost("{contractId:guid}/signature-set")]
    [Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> SupportStart(Guid contractId, [FromBody] StartSignatureSetRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ExpectedPageCount is not { } pages) return Invalid("expectedPageCount", "'expectedPageCount' is required.");
        return Respond(await signatures.StartAsync(SignatureActor.Support, user, contractId, new StartSignatureSetInput(pages, request.SignedOn, request.Note), cancellationToken));
    }

    [HttpPut("{contractId:guid}/signature-set/pages/{pageNumber:int}")]
    [Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> SupportPutPage(Guid contractId, int pageNumber, [FromBody] SignedPageRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await signatures.PutPageAsync(SignatureActor.Support, user, contractId, pageNumber, request.FileId ?? Guid.Empty, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{contractId:guid}/signature-set/finalize")]
    [Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
    [ProducesResponseType(typeof(ApiResponse<SignatureSetView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> SupportFinalize(Guid contractId, [FromBody] FinalizeSignatureSetRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await signatures.FinalizeAsync(SignatureActor.Support, user, contractId, request?.ExpectedRevision, cancellationToken))
            : UnauthorizedFailure();

    private string CurrentRole() => new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician }.FirstOrDefault(IsInRole) ?? "UNKNOWN";
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

/// <summary>Customer side of the 30/70 plan: pay an installment in the app, read the signed paper pages of their own contracts.</summary>
[ApiController]
[Route("api/customers/me/contracts/{contractId:guid}")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerContractPaymentPlanController(InstallmentPaymentHandlers installments, PaperSignatureHandlers signatures, ContractHandlers contracts,
    ContractDocumentHandlers documents) : OperationResultControllerBase
{
    [HttpPost("installments/{sequence:int}/payment")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> StartPayment(Guid contractId, int sequence, [FromBody] InstallmentPaymentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.StartAsync(InstallmentPayer.Customer, user, contractId, sequence, request.Method, cancellationToken))
            : UnauthorizedFailure();

    [HttpGet("installments/{sequence:int}/payment")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> GetPayment(Guid contractId, int sequence, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.GetAsync(InstallmentPayer.Customer, user, contractId, sequence, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("installments/{sequence:int}/payment/cancel")]
    [ProducesResponseType(typeof(ApiResponse<InstallmentPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> CancelPayment(Guid contractId, int sequence, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await installments.CancelAsync(InstallmentPayer.Customer, user, contractId, sequence, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("signature-sets")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SignatureSetView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SignatureSets(Guid contractId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var contract = await contracts.GetForCustomerAsync(user, contractId, cancellationToken);
        if (!contract.Succeeded) return Respond(contract);
        return Ok(ApiResponse<IReadOnlyList<SignatureSetView>>.Success(await signatures.ListAsync(contractId, cancellationToken), HttpContext.GetTraceId()));
    }

    [HttpGet("signed-pages/{fileId:guid}/url")]
    [ProducesResponseType(typeof(ApiResponse<AttachedFileUrlView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SignedPageUrl(Guid contractId, Guid fileId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.SignedPageUrlAsync(ContractViewer.Customer, user, RoleCodes.Customer, contractId, fileId, parsed, cancellationToken));
    }
}

/// <summary>
/// Collection follow-up of overdue installments (Sales, Manager, Admin). Recording never changes money or the contractual due date; legal review and
/// resolution are Manager/Admin. No refund or repossession action exists here.
/// </summary>
[ApiController]
[Route("api/collections")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CollectionsController(CollectionHandlers collections) : OperationResultControllerBase
{
    /// <summary>Cases, earliest due first, paged. Filters: <c>status</c>, <c>openOnly</c> (default true), <c>customerId</c>, <c>contractId</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CollectionCaseView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? status, [FromQuery] bool? openOnly,
        [FromQuery] Guid? customerId, [FromQuery] Guid? contractId, CancellationToken cancellationToken)
    {
        var paging = PageRequest.Parse(page ?? 1, pageSize ?? PageRequest.DefaultPageSize);
        if (!paging.Succeeded) return Respond(paging);
        return Respond(await collections.PageAsync(new CollectionCaseFilter(string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), openOnly ?? true,
            customerId, contractId), paging.Value!, cancellationToken));
    }

    [HttpGet("{caseId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CollectionCaseView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> Get(Guid caseId, CancellationToken cancellationToken) => Respond(await collections.GetAsync(caseId, cancellationToken));

    /// <summary>CONTACT | NEGOTIATION | AGREEMENT (promisedPaymentDate required) | NOTE, with summary, optional channel and evidence.</summary>
    [HttpPost("{caseId:guid}/activities")]
    [ProducesResponseType(typeof(ApiResponse<CollectionCaseView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Record(Guid caseId, [FromBody] CollectionActivityRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await collections.RecordActivityAsync(user, CurrentRole(), caseId, new CollectionActivityInput(request.Kind, request.Channel, request.Summary,
                request.PromisedPaymentDate, request.EvidenceFileIds, request.ExpectedRevision), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{caseId:guid}/escalate")]
    [ProducesResponseType(typeof(ApiResponse<CollectionCaseView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Escalate(Guid caseId, [FromBody] CollectionDecisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await collections.EscalateAsync(user, CurrentRole(), caseId, request.Reason, request.ExpectedRevision, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{caseId:guid}/legal-review")]
    [Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
    [ProducesResponseType(typeof(ApiResponse<CollectionCaseView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> LegalReview(Guid caseId, [FromBody] CollectionDecisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await collections.RequestLegalReviewAsync(user, CurrentRole(), caseId, request.Reason, request.ExpectedRevision, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{caseId:guid}/resolve")]
    [Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
    [ProducesResponseType(typeof(ApiResponse<CollectionCaseView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Resolve(Guid caseId, [FromBody] CollectionDecisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await collections.ResolveAsync(user, CurrentRole(), caseId, request.Reason, request.ExpectedRevision, cancellationToken))
            : UnauthorizedFailure();

    private string CurrentRole() => new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales }.FirstOrDefault(IsInRole) ?? "UNKNOWN";
}

/// <summary>Admin finance overview of installation contracts (30/70 plan figures, period money by method, unapplied and refunded).</summary>
[ApiController]
[Route("api/admin/finance")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class AdminFinanceController(FinanceOverview overview) : OperationResultControllerBase
{
    /// <summary><c>from</c> / <c>to</c>: local dates (default this month, at most 366 days); <c>zoneId</c>: operational zone of the installation.</summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(ApiResponse<FinanceOverviewView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? zoneId, CancellationToken cancellationToken)
        => Respond(await overview.GetAsync(from, to, zoneId, cancellationToken));
}
