using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Staff contract endpoints. Reads (2026-11-19): Sales, Technician, Manager and Admin read every contract (read-all; detail reads are audited).
/// Writes of the previous flow (official PDF, scan, review, amendments) stay Manager/Admin and apply to legacy (full-payment) contracts only; 30/70
/// contracts answer 409 CONTRACT_PAYMENT_PLAN_NOT_APPLICABLE there (they are signed on paper through the technician's signed-page upload).
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Technician},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class SalesContractsController(ContractHandlers handlers, ContractDocumentHandlers documents) : OperationResultControllerBase
{
    /// <summary>
    /// Only for an ACCEPTED quotation that has no contract yet (accepted before the automatic draft existed); otherwise 409 CONTRACT_ALREADY_EXISTS.
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/contracts")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Create(Guid quotationId, [FromBody] ContractTermsRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateAsync(user, quotationId, request.Terms, cancellationToken, request.WarrantyMonths), StatusCodes.Status201Created) : UnauthorizedFailure();

    [HttpGet("survey-requests/{surveyRequestId:guid}/contracts")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ListForSurveyRequestAsync(user, IsManager(), surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("contracts/{contractId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var result = await handlers.GetForStaffAsync(user, IsManager(), contractId, cancellationToken);
        if (result.Succeeded) await documents.AuditAsync(contractId, user, CurrentRole(), ContractAccessActions.Detail, null, cancellationToken);
        return Respond(result);
    }

    [HttpPut("contracts/{contractId:guid}")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> UpdateTerms(Guid contractId, [FromBody] ContractTermsRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.UpdateTermsAsync(user, contractId, revision, request.Terms, cancellationToken, request.WarrantyMonths));
    }

    [HttpPost("contracts/{contractId:guid}/issue")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Issue(Guid contractId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.IssueAsync(user, contractId, request.Revision, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// Discards the quotation's initial DRAFT contract (Manager/Admin, reason required). The row stays as DISCARDED; the survey request can
    /// then be closed. Issued, signed or paid contracts, and amendment drafts, cannot be discarded (409).
    /// </summary>
    [HttpPost("contracts/{contractId:guid}/discard")]
    [Authorize(Roles = StaffRoles)]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.CustomerContracts.ContractView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Discard(Guid contractId, [FromBody] DiscardContractRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.DiscardAsync(user, contractId, revision, request.Reason, cancellationToken));
    }

    [HttpPut("contracts/{contractId:guid}/official-document")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> AttachOfficialDocument(Guid contractId, [FromBody] OfficialDocumentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.AttachOfficialDocumentAsync(user, contractId, request.Revision, request.FileId, request.CanonicalHash ?? "", cancellationToken))
            : UnauthorizedFailure();

    /// <summary>
    /// A presigned GET URL (valid for <c>Storage:S3:PresignedUrlTtlSeconds</c>) for the official PDF of this contract version (any version the
    /// caller may read, superseded ones included); <c>disposition</c> is <c>inline</c> (default) or <c>attachment</c>. Sent with <c>Cache-Control: no-store</c>.
    /// </summary>
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("contracts/{contractId:guid}/official-document/url")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> OfficialDocumentUrl(Guid contractId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.OfficialDocumentUrlAsync(ContractViewer.Staff, user, IsManager(), contractId, parsed, cancellationToken));
    }

    /// <summary>A presigned GET URL for one signed scan the customer submitted (see <c>submissions</c> of the contract). Sent with <c>Cache-Control: no-store</c>.</summary>
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("contracts/{contractId:guid}/submissions/{submissionId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SubmissionUrl(Guid contractId, Guid submissionId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.SubmissionUrlAsync(ContractViewer.Staff, user, IsManager(), contractId, submissionId, parsed, cancellationToken));
    }

    [HttpPost("contracts/{contractId:guid}/publish")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Publish(Guid contractId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.PublishAsync(user, contractId, request.Revision, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Internal review of the hand-signed scan (not electronic signature verification).</summary>
    [HttpPost("contracts/{contractId:guid}/review")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Review(Guid contractId, [FromBody] ContractReviewRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var decision = request.Decision?.Trim().ToUpperInvariant() switch { "APPROVE" => ReviewDecision.Approve, "REJECT" => ReviewDecision.Reject, _ => (ReviewDecision?)null };
        if (decision is null) return Invalid("decision", "'decision' must be APPROVE or REJECT.");
        return Respond(await handlers.ReviewAsync(user, true, contractId, request.Revision, decision.Value, request.Reason, cancellationToken));
    }

    /// <summary>
    /// Paper contract: the Sales owner of the request (or a Manager/Admin) uploads the scan of the contract the customer signed in person
    /// (VERIFIED <c>SIGNED_CONTRACT</c> PDF registered by the caller). The contract moves AWAITING_SIGNATURE / SIGNATURE_REJECTED → UNDER_REVIEW
    /// exactly like a customer submission; a Manager/Admin still confirms the signature (<c>POST …/review</c>). 404 for Sales of another request.
    /// </summary>
    [HttpPost("contracts/{contractId:guid}/signed-document")]
    [Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> SubmitPaperSigned(Guid contractId, [FromBody] PaperSignedDocumentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.SubmitPaperSignedDocumentAsync(user, IsManager(), contractId, request.FileId, request.OfficialDocumentSha256 ?? "",
                request.SignedOn, request.Note, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("contracts/{contractId:guid}/amendments")]
    [Authorize(Roles = StaffRoles)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Amend(Guid contractId, [FromBody] ContractTermsRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateAmendmentAsync(user, contractId, request.Terms, cancellationToken, request.WarrantyMonths), StatusCodes.Status201Created) : UnauthorizedFailure();

    private bool IsManager() => IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Admin);

    private string CurrentRole() => new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician }.FirstOrDefault(IsInRole) ?? "UNKNOWN";

    private const string StaffRoles = $"{RoleCodes.Manager},{RoleCodes.Admin}";
}

/// <summary>Customer: published contracts (download metadata for the FE) and submission of the hand-signed scan.</summary>
[ApiController]
[Route("api/customers/me/contracts")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerContractsController(ContractHandlers handlers, ContractDocumentHandlers documents) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<ContractView>>.Success(await handlers.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{contractId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForCustomerAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// Retired (2026-11-19): the customer no longer uploads a signed scan. The paper contract is signed on site after the deposit and the technician
    /// uploads the photographed pages; the customer reads them with <c>GET …/signature-sets</c>.
    /// </summary>
    [HttpPost("{contractId:guid}/signed-document")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult SubmitSigned(Guid contractId)
        => Retired("None for the customer: the technician uploads the signed paper pages (POST /api/technician/contracts/{contractId}/signature-set …)");

    /// <summary>
    /// A presigned GET URL for the official PDF of one of the customer's own contract versions (from AWAITING_SIGNATURE on, superseded versions
    /// included as history). Downloading never changes the contract: paying still needs the SIGNED status through the payment endpoints.
    /// </summary>
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("{contractId:guid}/official-document/url")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> OfficialDocumentUrl(Guid contractId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.OfficialDocumentUrlAsync(ContractViewer.Customer, user, false, contractId, parsed, cancellationToken));
    }

    /// <summary>A presigned GET URL for one signed scan the customer submitted for this version.</summary>
    [ProducesResponseType(typeof(ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("{contractId:guid}/submissions/{submissionId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> SubmissionUrl(Guid contractId, Guid submissionId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await documents.SubmissionUrlAsync(ContractViewer.Customer, user, false, contractId, submissionId, parsed, cancellationToken));
    }
}
