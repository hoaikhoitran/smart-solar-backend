using System.Globalization;
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

/// <summary>Managers (and Admins): installation planning — project, team and shifts, milestones, checklist.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerInstallationsController(InstallationHandlers handlers) : OperationResultControllerBase
{
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
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(ApiResponse<IReadOnlyList<InstallationProjectView>>.Success(await handlers.ListProjectsAsync(status?.Trim().ToUpperInvariant(), cancellationToken), HttpContext.GetTraceId()));

    [HttpGet("installation-projects/{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken) => Respond(await handlers.GetForManagerAsync(projectId, cancellationToken));

    /// <summary>Checks every technician × shift (working schedule, leave, earliest departure, surveys, other installations, travel) without booking.</summary>
    [HttpPost("installation-projects/{projectId:guid}/schedule/preview")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationPreviewPolicy)]
    public async Task<IActionResult> Preview(Guid projectId, [FromBody] SchedulePlanRequest request, CancellationToken cancellationToken)
    {
        if (!TryShifts(request, out var shifts, out var error)) return error!;
        return Respond(await handlers.PreviewScheduleAsync(projectId, request.Members ?? [], request.LeaderUserId, shifts!, cancellationToken));
    }

    /// <summary>Books the whole team on all shifts atomically (replacing future shifts of the project).</summary>
    [HttpPut("installation-projects/{projectId:guid}/schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Confirm(Guid projectId, [FromBody] SchedulePlanRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        if (!TryShifts(request, out var shifts, out var error)) return error!;
        return Respond(await handlers.ConfirmScheduleAsync(user, projectId, revision, request.Members ?? [], request.LeaderUserId, shifts!, cancellationToken));
    }

    [HttpPut("installation-projects/{projectId:guid}/leader")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Leader(Guid projectId, [FromBody] LeaderRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ChangeLeaderAsync(user, projectId, request.Revision, request.LeaderUserId, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("installation-projects/{projectId:guid}/milestones")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Milestones(Guid projectId, [FromBody] MilestonePlanRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.ReplaceMilestonesAsync(user, projectId, request.Revision,
                (request.Milestones ?? []).Select(m => new MilestoneInput(m.MilestoneId, m.Name ?? "", m.Description, m.WeightPercent)).ToList(), request.Reason, cancellationToken))
            : UnauthorizedFailure();

    [HttpPut("installation-projects/{projectId:guid}/checklist/{itemId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Checklist(Guid projectId, Guid itemId, [FromBody] ChecklistRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.UpdateChecklistItemAsync(user, projectId, itemId, request.Revision,
                new ChecklistUpdate(request.IsRequired, request.Status ?? "", request.EvidenceFileId, request.Note), cancellationToken))
            : UnauthorizedFailure();

    [HttpGet("installation-projects/{projectId:guid}/handover")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Handover(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetHandoverAsync(user, true, projectId, cancellationToken)) : UnauthorizedFailure();

    private bool TryShifts(SchedulePlanRequest request, out IReadOnlyList<ShiftInput>? shifts, out IActionResult? error)
    {
        var list = new List<ShiftInput>();
        shifts = null;
        error = null;
        foreach (var (s, i) in (request.Shifts ?? []).Select((s, i) => (s, i)))
        {
            if (!DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !TimeOnly.TryParseExact(s.Start, "HH:mm", out var start) || !TimeOnly.TryParseExact(s.End, "HH:mm", out var end))
            {
                error = Invalid($"shifts[{i}]", "Each shift needs date (yyyy-MM-dd), start and end (HH:mm).");
                return false;
            }
            list.Add(new ShiftInput(date, start, end));
        }
        shifts = list;
        return true;
    }
}

/// <summary>Technicians: projects they are on. Execution actions are restricted to the project leader.</summary>
[ApiController]
[Route("api/technicians/me/installation-projects")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianInstallationsController(InstallationHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<InstallationProjectView>>.Success(await handlers.ListForTechnicianAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForTechnicianAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/start")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Start(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.StartProjectAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/milestones/{milestoneId:guid}/start")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> StartMilestone(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.StartMilestoneAsync(user, projectId, milestoneId, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("{projectId:guid}/milestones/{milestoneId:guid}/progress")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Progress(Guid projectId, Guid milestoneId, [FromBody] ProgressRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.UpdateProgressAsync(user, projectId, milestoneId, request.Revision, request.ProgressPercent, request.Notes, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Links a verified INSTALLATION_EVIDENCE photo uploaded by the leader.</summary>
    [HttpPost("{projectId:guid}/milestones/{milestoneId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Evidence(Guid projectId, Guid milestoneId, [FromBody] EvidenceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.AddEvidenceAsync(user, projectId, milestoneId, request.FileId, request.Caption, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/milestones/{milestoneId:guid}/complete")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Complete(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CompleteMilestoneAsync(user, projectId, milestoneId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/milestones/{milestoneId:guid}/reopen")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Reopen(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ReopenMilestoneAsync(user, projectId, milestoneId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/assets")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Assets(Guid projectId, [FromBody] AssetsRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.RegisterAssetsAsync(user, projectId, (request.Assets ?? []).Select(a => new AssetInput(a.AssetType ?? "", a.ProductId, a.Manufacturer ?? "",
                a.Model ?? "", a.SerialNumber ?? "", a.PositionCode, a.WarrantyMonths)).ToList(), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/acceptance-request")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> RequestAcceptance(Guid projectId, [FromBody] AcceptanceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RequestAcceptanceAsync(user, projectId, request.CompletionReport ?? "", cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Customers: their installation timeline, evidence, acceptance decision and handover.</summary>
[ApiController]
[Route("api/customers/me/installation-projects")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerInstallationsController(InstallationHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(ApiResponse<IReadOnlyList<InstallationProjectView>>.Success(await handlers.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{projectId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForCustomerAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{projectId:guid}/acceptance")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Decide(Guid projectId, [FromBody] AcceptanceDecisionRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("ACCEPT" or "REQUEST_REWORK")) return Invalid("decision", "'decision' must be ACCEPT or REQUEST_REWORK.");
        return Respond(await handlers.DecideAcceptanceAsync(user, projectId, decision == "ACCEPT", request.Reason, cancellationToken));
    }

    [HttpGet("{projectId:guid}/handover")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Handover(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetHandoverAsync(user, false, projectId, cancellationToken)) : UnauthorizedFailure();
}
