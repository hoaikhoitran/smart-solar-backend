using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// The customer's uncovered-zone dispatch request for one of their survey requests (404 for anything they do not own): create
/// (201, or 200 with the open one), read (status + live proposal), accept / decline a proposal, cancel.
/// </summary>
[ApiController]
[Route("api/customers/me/survey-requests/{surveyRequestId:guid}/dispatch-request")]
[Authorize(Roles = RoleCodes.Customer)]
public sealed class CustomerDispatchRequestsController(DispatchService service) : SchedulingControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Create(Guid surveyRequestId, [FromBody] CreateDispatchRequestRequest? request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var windows = new List<PreferredWindowInput>();
        var items = request?.PreferredWindows ?? [];
        if (items.Count > DispatchService.MaxPreferredWindows)
            return InvalidField("preferredWindows", $"At most {DispatchService.MaxPreferredWindows} preferred windows are allowed.");
        for (var i = 0; i < items.Count; i++)
        {
            if (!IsoTimestamp.TryParse(items[i]?.StartsAt, out var start) || !IsoTimestamp.TryParse(items[i]?.EndsAt, out var end))
                return InvalidField($"preferredWindows[{i}]", "'startsAt' and 'endsAt' must be ISO 8601 with an offset, e.g. 2026-10-20T08:00:00+07:00.");
            windows.Add(new PreferredWindowInput(start, end));
        }
        var result = await service.CreateAsync(new CreateDispatchRequestCommand(userId, surveyRequestId, windows, request?.Note), cancellationToken);
        return Respond(result, result.Value is { Created: true } ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await service.GetForCustomerAsync(userId, surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("proposals/{proposalId:guid}/accept")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Accept(Guid surveyRequestId, Guid proposalId, [FromBody] AcceptDispatchProposalRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.AcceptAsync(new RespondToProposalCommand(userId, surveyRequestId, proposalId, request?.ExpectedRevision, null), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("proposals/{proposalId:guid}/decline")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Decline(Guid surveyRequestId, Guid proposalId, [FromBody] DeclineDispatchProposalRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.DeclineAsync(new RespondToProposalCommand(userId, surveyRequestId, proposalId, request?.ExpectedRevision, request?.Reason), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("cancel")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Cancel(Guid surveyRequestId, [FromBody] CancelDispatchRequestRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.CancelAsync(new CancelDispatchCommand(userId, surveyRequestId, request?.Reason), cancellationToken))
            : UnauthorizedFailure();
}

/// <summary>
/// Manager/Admin dispatch queue (SLA indicators), detail, candidates (known schedule facts only), direct assignment, proposals,
/// withdrawal, unserviceable and close. Cross-zone technicians need crossZoneAuthorized = true and a reason.
/// </summary>
[ApiController]
[Route("api/admin/dispatch-requests")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
public sealed class AdminDispatchRequestsController(DispatchService service) : SchedulingControllerBase
{
    /// <summary>GET ?status=&amp;reason=&amp;overdue=&amp;page=1&amp;pageSize=20 — oldest review deadline first.</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? reason, [FromQuery] bool? overdue, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (normalizedStatus is not null && !DispatchStatuses.All.Contains(normalizedStatus))
            return InvalidField("status", $"'status' must be one of {string.Join(", ", DispatchStatuses.All)}.");
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim().ToUpperInvariant();
        if (normalizedReason is not null && !DispatchReasons.All.Contains(normalizedReason))
            return InvalidField("reason", $"'reason' must be one of {string.Join(", ", DispatchReasons.All)}.");
        return Ok(await service.ListAsync(normalizedStatus, normalizedReason, overdue, page, pageSize, cancellationToken));
    }

    [HttpGet("{dispatchRequestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get(Guid dispatchRequestId, CancellationToken cancellationToken)
        => Respond(await service.GetAsync(dispatchRequestId, cancellationToken));

    /// <summary>GET {id}/candidates?startsAt=2026-10-20T08:00:00+07:00 — zone members first, then other zones flagged crossZone.</summary>
    [HttpGet("{dispatchRequestId:guid}/candidates")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Candidates(Guid dispatchRequestId, [FromQuery] string? startsAt, CancellationToken cancellationToken)
    {
        if (!IsoTimestamp.TryParse(startsAt, out var start))
            return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset, e.g. 2026-10-20T08:00:00+07:00.");
        return Respond(await service.CandidatesAsync(dispatchRequestId, start, cancellationToken));
    }

    [HttpPost("{dispatchRequestId:guid}/assign")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Assign(Guid dispatchRequestId, [FromBody] DispatchTechnicianRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (Command(userId, dispatchRequestId, request, out var command) is { } invalid) return invalid;
        return Respond(await service.AssignAsync(command!, cancellationToken));
    }

    [HttpPost("{dispatchRequestId:guid}/proposals")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Propose(Guid dispatchRequestId, [FromBody] DispatchTechnicianRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (Command(userId, dispatchRequestId, request, out var command) is { } invalid) return invalid;
        return Respond(await service.ProposeAsync(command!, cancellationToken), StatusCodes.Status201Created);
    }

    [HttpPost("{dispatchRequestId:guid}/proposals/{proposalId:guid}/withdraw")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Withdraw(Guid dispatchRequestId, Guid proposalId, [FromBody] WithdrawDispatchProposalRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.WithdrawProposalAsync(new WithdrawProposalCommand(userId, dispatchRequestId, proposalId, request?.Reason), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{dispatchRequestId:guid}/unserviceable")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Unserviceable(Guid dispatchRequestId, [FromBody] ResolveDispatchRequestRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.MarkUnserviceableAsync(new ResolveDispatchCommand(userId, dispatchRequestId, request?.Reason, request?.ExpectedRevision), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{dispatchRequestId:guid}/close")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Close(Guid dispatchRequestId, [FromBody] ResolveDispatchRequestRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await service.CloseAsync(new ResolveDispatchCommand(userId, dispatchRequestId, request?.Reason, request?.ExpectedRevision), cancellationToken))
            : UnauthorizedFailure();

    private IActionResult? Command(Guid userId, Guid dispatchRequestId, DispatchTechnicianRequest request, out DispatchTechnicianCommand? command)
    {
        command = null;
        if (request.TechnicianUserId == Guid.Empty) return InvalidField("technicianUserId", "'technicianUserId' is required.");
        if (!IsoTimestamp.TryParse(request.StartsAt, out var start))
            return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset, e.g. 2026-10-20T08:00:00+07:00.");
        if (request.ExpectedRevision is not { } revision || revision < 0) return InvalidField("expectedRevision", "'expectedRevision' is required.");
        if (request.Reason is { Length: > DispatchService.MaxReasonLength }) return InvalidField("reason", $"'reason' must be at most {DispatchService.MaxReasonLength} characters.");
        command = new DispatchTechnicianCommand(userId, dispatchRequestId, request.TechnicianUserId, start, request.CrossZoneAuthorized == true, request.Reason, revision,
            request.Note);
        return null;
    }
}
