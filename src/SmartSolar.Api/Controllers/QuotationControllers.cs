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

/// <summary>Sales: quotations for the survey requests they own (issued without manager approval). Managers/Admins may read.</summary>
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
    public async Task<IActionResult> Issue(Guid quotationId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.IssueAsync(user, quotationId, request.Revision, cancellationToken)) : UnauthorizedFailure();

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

    [HttpPost("{quotationId:guid}/revision-request")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> RequestRevision(Guid quotationId, [FromBody] ReasonRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RequestRevisionAsync(user, quotationId, request.Reason ?? "", cancellationToken)) : UnauthorizedFailure();
}
