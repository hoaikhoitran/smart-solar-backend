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

/// <summary>Sales (owner) prepares, issues and publishes contracts; Sales or Managers review signed scans.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class SalesContractsController(ContractHandlers handlers) : OperationResultControllerBase
{
    [HttpPost("quotations/{quotationId:guid}/contracts")]
    [Authorize(Roles = RoleCodes.Sales)]
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
        => CurrentUserId() is { } user ? Respond(await handlers.GetForStaffAsync(user, IsManager(), contractId, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("contracts/{contractId:guid}")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> UpdateTerms(Guid contractId, [FromBody] ContractTermsRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.UpdateTermsAsync(user, contractId, revision, request.Terms, cancellationToken, request.WarrantyMonths));
    }

    [HttpPost("contracts/{contractId:guid}/issue")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Issue(Guid contractId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.IssueAsync(user, contractId, request.Revision, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("contracts/{contractId:guid}/official-document")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> AttachOfficialDocument(Guid contractId, [FromBody] OfficialDocumentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.AttachOfficialDocumentAsync(user, contractId, request.Revision, request.FileId, request.CanonicalHash ?? "", cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("contracts/{contractId:guid}/publish")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Publish(Guid contractId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.PublishAsync(user, contractId, request.Revision, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Internal review of the hand-signed scan (not electronic signature verification).</summary>
    [HttpPost("contracts/{contractId:guid}/review")]
    [Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager}")]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Review(Guid contractId, [FromBody] ContractReviewRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var decision = request.Decision?.Trim().ToUpperInvariant() switch { "APPROVE" => ReviewDecision.Approve, "REJECT" => ReviewDecision.Reject, _ => (ReviewDecision?)null };
        if (decision is null) return Invalid("decision", "'decision' must be APPROVE or REJECT.");
        return Respond(await handlers.ReviewAsync(user, IsInRole(RoleCodes.Manager), contractId, request.Revision, decision.Value, request.Reason, cancellationToken));
    }

    [HttpPost("contracts/{contractId:guid}/amendments")]
    [Authorize(Roles = RoleCodes.Sales)]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> Amend(Guid contractId, [FromBody] ContractTermsRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateAmendmentAsync(user, contractId, request.Terms, cancellationToken, request.WarrantyMonths), StatusCodes.Status201Created) : UnauthorizedFailure();

    private bool IsManager() => IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Admin);
}

/// <summary>Customer: published contracts (download metadata for the FE) and submission of the hand-signed scan.</summary>
[ApiController]
[Route("api/customers/me/contracts")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerContractsController(ContractHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<ContractView>>.Success(await handlers.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{contractId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.ContractReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForCustomerAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{contractId:guid}/signed-document")]
    [EnableRateLimiting(RateLimitingExtensions.ContractWritePolicy)]
    public async Task<IActionResult> SubmitSigned(Guid contractId, [FromBody] SignedDocumentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.SubmitSignedDocumentAsync(user, contractId, request.FileId, request.OfficialDocumentSha256 ?? "", cancellationToken))
            : UnauthorizedFailure();
}
