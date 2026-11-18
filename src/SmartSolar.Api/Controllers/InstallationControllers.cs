using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>Managers (and Admins): projects from PAID contracts, responsible-technician assignment, cancellation, checklist, handover.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerInstallationsController(InstallationHandlers handlers, InstallationEvidenceHandlers evidence, EnergyMonitoringHandlers monitoring)
    : OperationResultControllerBase
{
    /// <summary>Energy monitoring of a project: SIMULATED expected production only (no measured data source yet; alerts NOT_CONFIGURED).</summary>
    [HttpGet("installation-projects/{projectId:guid}/energy-monitoring")]
    [ProducesResponseType(typeof(ApiResponse<EnergyMonitoringView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EnergyMonitoring(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await monitoring.GetAsync(user, true, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Contracts whose full payment is verified and that have no installation project yet.</summary>
    [HttpGet("installations/ready-contracts")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> ReadyContracts(CancellationToken cancellationToken)
        => Ok(ApiResponse<IReadOnlyList<PaidContractSource>>.Success(await handlers.ListReadyContractsAsync(cancellationToken), HttpContext.GetTraceId()));

    [HttpPost("contracts/{contractId:guid}/installation-project")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Create(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateProjectAsync(user, contractId, cancellationToken), StatusCodes.Status201Created) : UnauthorizedFailure();

    [HttpGet("installation-projects")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        // Opt-in paging: without page/pageSize the historical array is returned unchanged.
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        var filter = status?.Trim().ToUpperInvariant();
        return paging.Value is { } p
            ? Ok(ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<InstallationProjectSummary>>.Success(await handlers.PageProjectsAsync(filter, p, cancellationToken), HttpContext.GetTraceId()))
            : Ok(ApiResponse<IReadOnlyList<InstallationProjectSummary>>.Success(await handlers.ListProjectsAsync(filter, cancellationToken), HttpContext.GetTraceId()));
    }

    [HttpGet("installation-projects/{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken) => Respond(await handlers.GetForManagerAsync(projectId, cancellationToken));

    /// <summary>Technicians of the site's service areas for an interval, with eligibility reason codes (no booking is made).</summary>
    [HttpGet("installation-projects/{projectId:guid}/technician-candidates")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationPreviewPolicy)]
    public async Task<IActionResult> Candidates(Guid projectId, [FromQuery] DateTimeOffset? startsAt, [FromQuery] DateTimeOffset? endsAt, CancellationToken cancellationToken)
    {
        if (startsAt is not { } start || endsAt is not { } end) return Invalid("startsAt", "'startsAt' and 'endsAt' (ISO-8601 with offset) are required.");
        return Respond(await handlers.GetCandidatesAsync(projectId, start, end, cancellationToken));
    }

    /// <summary>Assign, reassign or reschedule the responsible technician (the technician must accept again).</summary>
    [HttpPut("installation-projects/{projectId:guid}/assignment")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Assign(Guid projectId, [FromBody] AssignTechnicianRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.TechnicianUserId == Guid.Empty) return Invalid("technicianUserId", "'technicianUserId' is required.");
        if (request.ScheduledStartsAt is not { } start || request.ScheduledEndsAt is not { } end)
            return Invalid("scheduledStartsAt", "'scheduledStartsAt' and 'scheduledEndsAt' are required.");
        if (request.ExpectedRevision is not { } revision) return Invalid("expectedRevision", "'expectedRevision' is required.");
        return Respond(await handlers.AssignAsync(user, projectId, new AssignTechnicianInput(request.TechnicianUserId, start, end, request.Reason, revision,
            request.CrossZoneAuthorized == true), cancellationToken));
    }

    /// <summary>Cancel before work starts (reason required). Contract and payment are not changed; refunds are a manual business process.</summary>
    [HttpPost("installation-projects/{projectId:guid}/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Cancel(Guid projectId, [FromBody] CancelInstallationRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ExpectedRevision is not { } revision) return Invalid("expectedRevision", "'expectedRevision' is required.");
        return Respond(await handlers.CancelAsync(user, projectId, revision, request.Reason, cancellationToken));
    }

    /// <summary>
    /// Awaiting internal acceptance (stored PENDING_CUSTOMER_ACCEPTANCE) → REWORK_REQUIRED (reason required, ≤500): closes the pending acceptance
    /// round as REWORK_REQUESTED decided by the manager; the next completion report needs new evidence.
    /// </summary>
    [HttpPost("installation-projects/{projectId:guid}/return-to-rework")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ReturnToRework(Guid projectId, [FromBody] ReturnToReworkRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ExpectedRevision is not { } revision) return Invalid("expectedRevision", "'expectedRevision' is required.");
        if (request.Reason is { Length: > 500 } && request.Reason.Trim().Length > 500) return Invalid("reason", "'reason' must be at most 500 characters.");
        return Respond(await handlers.ReturnToReworkAsync(user, projectId, revision, request.Reason, cancellationToken));
    }

    /// <summary>
    /// Retired (2026-11-19): the customer accepts the installation (<c>POST /api/customers/me/installation-projects/{projectId}/acceptance</c>). A
    /// Manager/Admin coordinates rework (<c>…/return-to-rework</c>) but never accepts on the customer's behalf.
    /// </summary>
    [HttpPost("installation-projects/{projectId:guid}/acceptance")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult AcceptInternally(Guid projectId)
        => Retired("Customer: POST /api/customers/me/installation-projects/{projectId}/acceptance (ACCEPT | REQUEST_REWORK)");

    /// <summary>
    /// Manager (only) approves, with a reason, that the installed panels / inverters differ from the accepted quotation (see
    /// <c>equipment.differences</c>). Internal and final (INTERNALLY_APPROVED): the customer is informed, not asked.
    /// </summary>
    [HttpPost("installation-projects/{projectId:guid}/equipment-substitution/approve")]
    [Authorize(Roles = RoleCodes.Manager)]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ApproveSubstitution(Guid projectId, [FromBody] SubstitutionApprovalRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ExpectedRevision is not { } revision) return Invalid("expectedRevision", "'expectedRevision' is required.");
        if (request.Reason is { } reason && reason.Trim().Length > 400) return Invalid("reason", "'reason' must be at most 400 characters.");
        return Respond(await handlers.ApproveSubstitutionAsync(user, projectId, revision, request.Reason, cancellationToken));
    }

    [HttpPut("installation-projects/{projectId:guid}/checklist/{itemId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Checklist(Guid projectId, Guid itemId, [FromBody] ChecklistRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.UpdateChecklistItemAsync(user, projectId, itemId, request.Revision,
                new ChecklistUpdate(request.IsRequired, request.Status ?? "", request.EvidenceFileId, request.Note), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Evidence photo metadata (no storage locations). See <c>…/evidence/{evidenceId}/url</c> for a short-lived read URL.</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<System.Collections.Generic.IReadOnlyList<SmartSolar.Modules.Installations.InstallationEvidenceFileView>>), StatusCodes.Status200OK)]
    [HttpGet("installation-projects/{projectId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceList(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await evidence.GetEvidenceAsync(EvidenceViewerRole.Staff, user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// A presigned GET URL (valid for <c>Storage:S3:PresignedUrlTtlSeconds</c>) for one evidence photo; <c>disposition</c> is
    /// <c>inline</c> (default) or <c>attachment</c>. Sent with <c>Cache-Control: no-store</c>.
    /// </summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Installations.InstallationEvidenceUrlView>), StatusCodes.Status200OK)]
    [HttpGet("installation-projects/{projectId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid projectId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidence.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, user, projectId, evidenceId, parsed, cancellationToken));
    }

    [HttpGet("installation-projects/{projectId:guid}/handover")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Handover(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetHandoverAsync(user, true, projectId, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Technicians: only the responsible technician of a project sees it (others get 404) and acts on it.</summary>
[ApiController]
[Route("api/technicians/me/installation-projects")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianInstallationsController(InstallationHandlers handlers, InstallationEvidenceHandlers evidence) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<InstallationTechnicianView>>.Success(await handlers.ListForTechnicianAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForTechnicianAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/accept")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Accept(Guid projectId, [FromBody] AcceptAssignmentRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.AcceptAssignmentAsync(user, projectId, request?.ExpectedRevision, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/decline")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Decline(Guid projectId, [FromBody] DeclineAssignmentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.DeclineAssignmentAsync(user, projectId, request.Reason, request.ExpectedRevision, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/start")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Start(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.StartProjectAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Links a verified INSTALLATION_EVIDENCE photo uploaded by the responsible technician to the current round.</summary>
    [HttpPost("{projectId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Evidence(Guid projectId, [FromBody] EvidenceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.AddEvidenceAsync(user, projectId, request.FileId, request.Caption, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Evidence photo metadata (no storage locations). See <c>…/evidence/{evidenceId}/url</c> for a short-lived read URL.</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<System.Collections.Generic.IReadOnlyList<SmartSolar.Modules.Installations.InstallationEvidenceFileView>>), StatusCodes.Status200OK)]
    [HttpGet("{projectId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceList(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await evidence.GetEvidenceAsync(EvidenceViewerRole.Technician, user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// A presigned GET URL (valid for <c>Storage:S3:PresignedUrlTtlSeconds</c>) for one evidence photo; <c>disposition</c> is
    /// <c>inline</c> (default) or <c>attachment</c>. Sent with <c>Cache-Control: no-store</c>.
    /// </summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Installations.InstallationEvidenceUrlView>), StatusCodes.Status200OK)]
    [HttpGet("{projectId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid projectId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidence.CreateEvidenceUrlAsync(EvidenceViewerRole.Technician, user, projectId, evidenceId, parsed, cancellationToken));
    }

    [HttpPost("{projectId:guid}/assets")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Assets(Guid projectId, [FromBody] AssetsRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.RegisterAssetsAsync(user, projectId, (request.Assets ?? []).Select(a => new AssetInput(a.AssetType ?? "", a.ProductId, a.Manufacturer ?? "",
                a.Model ?? "", a.SerialNumber, a.PositionCode, a.WarrantyMonths, a.Quantity)).ToList(), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/completion-report")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CompletionReport(Guid projectId, [FromBody] CompletionReportRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ReportCompletionAsync(user, projectId, request.CompletionReport, request.ExpectedRevision, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>
/// Customers follow their installation: progress, schedule, evidence, handover (no staff-only data). Read-only for decisions since 2026-11-18: the
/// completion is accepted internally by SmartSolar and equipment substitutions are approved by a Manager; both former customer steps answer 410.
/// </summary>
[ApiController]
[Route("api/customers/me/installation-projects")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerInstallationsController(InstallationHandlers handlers, InstallationEvidenceHandlers evidence, EnergyMonitoringHandlers monitoring)
    : OperationResultControllerBase
{
    /// <summary>Energy monitoring of the customer's own project: SIMULATED expected production only, never presented as measured.</summary>
    [HttpGet("{projectId:guid}/energy-monitoring")]
    [ProducesResponseType(typeof(ApiResponse<EnergyMonitoringView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EnergyMonitoring(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await monitoring.GetAsync(user, false, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<InstallationCustomerView>>.Success(await handlers.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForCustomerAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Evidence photo metadata (no storage locations). See <c>…/evidence/{evidenceId}/url</c> for a short-lived read URL.</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<System.Collections.Generic.IReadOnlyList<SmartSolar.Modules.Installations.InstallationEvidenceFileView>>), StatusCodes.Status200OK)]
    [HttpGet("{projectId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceList(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await evidence.GetEvidenceAsync(EvidenceViewerRole.Customer, user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// A presigned GET URL (valid for <c>Storage:S3:PresignedUrlTtlSeconds</c>) for one evidence photo; <c>disposition</c> is
    /// <c>inline</c> (default) or <c>attachment</c>. Sent with <c>Cache-Control: no-store</c>.
    /// </summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Installations.InstallationEvidenceUrlView>), StatusCodes.Status200OK)]
    [HttpGet("{projectId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid projectId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidence.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, user, projectId, evidenceId, parsed, cancellationToken));
    }

    /// <summary>
    /// Customer acceptance (restored 2026-11-19): <c>decision</c> ACCEPT, or REQUEST_REWORK / REJECT with <c>reason</c> (and optional VERIFIED
    /// <c>ACCEPTANCE_EVIDENCE</c> <c>evidenceFileIds</c>), on the pending round of a reported completion. ACCEPT completes the handover and, for a 30/70
    /// contract, opens the 70% balance (due within 72 hours). Only the project's customer; anyone else 404.
    /// </summary>
    [HttpPost("{projectId:guid}/acceptance")]
    [ProducesResponseType(typeof(ApiResponse<InstallationCustomerView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Decide(Guid projectId, [FromBody] CustomerAcceptanceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.DecideAcceptanceAsync(user, projectId, new CustomerAcceptanceInput(request.Decision, request.Reason, request.EvidenceFileIds,
                request.ExpectedRevision), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Retired (2026-11-18): a Manager approves equipment substitutions with a reason (internal, final); the customer is informed.</summary>
    [HttpPost("{projectId:guid}/equipment-substitution/approve")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult ApproveSubstitution(Guid projectId)
        => RetiredOnSite("None for the customer: POST /api/installation-projects/{projectId}/equipment-substitution/approve");

    [HttpGet("{projectId:guid}/handover")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Handover(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetHandoverAsync(user, false, projectId, cancellationToken)) : UnauthorizedFailure();
}
