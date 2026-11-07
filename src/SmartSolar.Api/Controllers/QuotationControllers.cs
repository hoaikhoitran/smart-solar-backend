using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Quotations;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Sales: quotations for the survey requests they own. Catalog prices without a discount are issued by Sales; a discount, a reduced or a
/// zero price needs a Manager approval of exactly that pricing first. Managers/Admins may read.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class SalesQuotationsController(QuotationHandlers handlers, QuotationOptions options) : OperationResultControllerBase
{
    [HttpPost("survey-requests/{surveyRequestId:guid}/quotations")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Create(Guid surveyRequestId, [FromBody] QuotationHeaderRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (!TryHeader(request, out var header, out var error)) return error!;
        return Respond(await handlers.CreateAsync(user, surveyRequestId, header!, cancellationToken), StatusCodes.Status201Created);
    }

    /// <summary>
    /// Runs (or repeats — idempotent) the automatic STANDARD quotation from the completed survey's bill of materials; normally it already
    /// ran when the technician completed the survey. Sales owner or Manager/Admin. Returns the outcome and the quotation, if any.
    /// </summary>
    [HttpPost("survey-requests/{surveyRequestId:guid}/quotations/generate")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<AutomaticQuotationResult>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Generate(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GenerateForStaffAsync(user, IsManager(), surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>The exception queue: AUTOMATIC drafts whose pricing needs Sales (see each quotation's pricingBasis.issues).</summary>
    [HttpGet("quotations/pending-review")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<IReadOnlyList<QuotationView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> PendingReview(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(SmartSolar.Api.Contracts.ApiResponse<IReadOnlyList<QuotationView>>.Success(
            await handlers.ListPendingReviewAsync(user, IsManager(), cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("survey-requests/{surveyRequestId:guid}/quotations")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> List(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ListForSurveyRequestAsync(user, IsManager(), surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("quotations/{quotationId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> Get(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForSalesAsync(user, IsManager(), quotationId, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("quotations/{quotationId:guid}")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Update(Guid quotationId, [FromBody] QuotationHeaderRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        if (!TryHeader(request, out var header, out var error)) return error!;
        return Respond(await handlers.UpdateHeaderAsync(user, quotationId, revision, header!, cancellationToken));
    }

    [HttpPost("quotations/{quotationId:guid}/lines")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> AddLine(Guid quotationId, [FromBody] QuotationLineRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.AddLineAsync(user, quotationId, request.Revision,
                new QuotationLineInput(request.ProductId, request.Name, request.Unit, request.Quantity, request.UnitPrice, request.Description), cancellationToken))
            : UnauthorizedFailure();

    [HttpPut("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> UpdateLine(Guid quotationId, Guid lineId, [FromBody] QuotationLineUpdateRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.UpdateLineAsync(user, quotationId, lineId, request.Revision, request.Quantity, request.UnitPrice, request.Description, cancellationToken))
            : UnauthorizedFailure();

    [HttpDelete("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> RemoveLine(Guid quotationId, Guid lineId, [FromQuery] int revision, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RemoveLineAsync(user, quotationId, lineId, revision, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("quotations/{quotationId:guid}/issue")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Issue(Guid quotationId, [FromBody] IssueQuotationRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.IssueAsync(user, quotationId, request.Revision, cancellationToken, request.ReviewNote)) : UnauthorizedFailure();

    /// <summary>
    /// The on-site survey was amended after this DRAFT was made (surveyChanged = true): after reviewing the lines, Sales re-bases the
    /// draft on the current survey version with a reason. Required before such a draft can be issued.
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/confirm-survey-version")]
    [Authorize(Roles = RoleCodes.Sales)]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> ConfirmSurveyVersion(Guid quotationId, [FromBody] ConfirmSurveyVersionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.ConfirmSurveyVersionAsync(user, quotationId, request.Revision, request.Reason, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>
    /// Asks a Manager to approve the draft's discount / reduced / zero prices (see pricingApproval.adjustments). The request names the
    /// exact current pricing; any later pricing edit needs a new request.
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/pricing-approval-request")]
    [Authorize(Roles = RoleCodes.Sales)]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> RequestPricingApproval(Guid quotationId, [FromBody] PricingApprovalRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.RequestPricingApprovalAsync(user, quotationId, request.Revision, request.Reason, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>New draft version copied from an issued quotation or one the customer sent back.</summary>
    [HttpPost("quotations/{quotationId:guid}/revisions")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> CreateRevision(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateRevisionAsync(user, quotationId, cancellationToken), StatusCodes.Status201Created) : UnauthorizedFailure();

    /// <summary>Links the FE-generated quotation PDF (a verified QUOTATION_DOCUMENT file).</summary>
    [HttpPut("quotations/{quotationId:guid}/document")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> AttachDocument(Guid quotationId, [FromBody] FileReferenceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.AttachDocumentAsync(user, quotationId, request.FileId, cancellationToken)) : UnauthorizedFailure();

    private bool IsManager() => IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Admin);

    private bool TryHeader(QuotationHeaderRequest r, out QuotationHeaderInput? header, out IActionResult? error)
    {
        header = null;
        error = null;
        DateTimeOffset? validUntil = null;
        if (r.ValidUntil is not null)
        {
            if (!IsoTimestamp.TryParse(r.ValidUntil, out var parsed)) { error = Invalid("validUntil", "'validUntil' must be ISO 8601 with an offset."); return false; }
            validUntil = parsed;
        }
        header = new QuotationHeaderInput(r.LaborCost, r.TransportCost, r.OtherServiceCost, r.DiscountAmount, r.TaxRatePercent ?? options.DefaultTaxRatePercent,
            validUntil, r.Terms, r.Notes);
        return true;
    }
}

/// <summary>
/// Manager: approves or rejects pricing exceptions (discounts, prices below the catalog reference, zero prices) of quotation drafts. Only
/// the Manager role decides (the approved role matrix names no other approver); the Sales owner never approves their own quotation.
/// </summary>
[ApiController]
[Route("api/manager/quotations")]
[Authorize(Roles = RoleCodes.Manager)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerQuotationApprovalsController(QuotationHandlers handlers) : OperationResultControllerBase
{
    /// <summary>Drafts whose current pricing waits for a decision.</summary>
    [HttpGet("pricing-approvals")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<IReadOnlyList<QuotationView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
        => CurrentUserId() is not null
            ? Ok(Contracts.ApiResponse<IReadOnlyList<QuotationView>>.Success(await handlers.ListPendingPricingApprovalsAsync(cancellationToken), HttpContext.GetTraceId()))
            : UnauthorizedFailure();

    [HttpPost("{quotationId:guid}/pricing-approval")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Decide(Guid quotationId, [FromBody] PricingApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Approve is not { } approve) return Invalid("approve", "'approve' (true or false) is required.");
        return Respond(await handlers.DecidePricingApprovalAsync(user, quotationId, request.Revision, approve, request.Reason, cancellationToken));
    }
}

/// <summary>Customer: own quotations (drafts are never visible), acceptance or revision request.</summary>
[ApiController]
[Route("api/customers/me/quotations")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerQuotationsController(QuotationHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<IReadOnlyList<QuotationView>>.Success(await handlers.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{quotationId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> Get(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForCustomerAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{quotationId:guid}/accept")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Accept(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.AcceptAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Rejects an issued quotation (final for this version; the request stays open for a new offer or its closure).</summary>
    [HttpPost("{quotationId:guid}/reject")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Reject(Guid quotationId, [FromBody] ReasonRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RejectAsync(user, quotationId, request?.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{quotationId:guid}/revision-request")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> RequestRevision(Guid quotationId, [FromBody] ReasonRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RequestRevisionAsync(user, quotationId, request.Reason ?? "", cancellationToken)) : UnauthorizedFailure();
}
