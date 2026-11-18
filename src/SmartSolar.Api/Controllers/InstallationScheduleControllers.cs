using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// The surveying technician records the installation date agreed with the customer in person, once the contract exists and is paid (contract created
/// from the paid quotation; SIGNED for a contract of the previous flow). PLANNED, confirmed by the system when the contract is PAID (paid and signed).
/// Only contracts whose on-site survey the caller completed are visible (others: 404).
/// </summary>
[ApiController]
[Route("api/technicians/me")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianInstallationSchedulesController(InstallationScheduleHandlers schedules) : OperationResultControllerBase
{
    [HttpGet("installation-schedules")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Ok(ApiResponse<IReadOnlyList<TechnicianScheduleContractView>>.Success(await schedules.ListForTechnicianAsync(user, cancellationToken), HttpContext.GetTraceId()))
            : UnauthorizedFailure();

    [HttpGet("contracts/{contractId:guid}/installation-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.GetForTechnicianAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Records or changes (reason required) the agreed date (200 with the live schedule). Repeating the same plan returns it unchanged.</summary>
    [HttpPost("contracts/{contractId:guid}/installation-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Propose(Guid contractId, [FromBody] InstallationScheduleRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.StartsAt is not { } start || request.EndsAt is not { } end) return Invalid("startsAt", "'startsAt' and 'endsAt' (ISO-8601 with offset) are required.");
        var result = await schedules.ProposeAsync(user, false, contractId,
            new ProposeInstallationScheduleInput(null, start, end, request.CustomerAgreementNote, request.Reason, request.ExpectedRevision), cancellationToken);
        return Respond(result);
    }

    [HttpPost("installation-schedules/{scheduleId:guid}/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Cancel(Guid scheduleId, [FromBody] InstallationScheduleReasonRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.CancelAsync(user, false, scheduleId, request?.Reason, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// Candidate installation intervals for the contract: for each date of [from, to] (≤ 31 dates, <c>yyyy-MM-dd</c>, local), the interval of
    /// <c>durationDays</c> working days starting that day and whether the caller can take it now (same checks as recording a plan). Read-only.
    /// </summary>
    [HttpGet("contracts/{contractId:guid}/installation-availability")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationPreviewPolicy)]
    public async Task<IActionResult> Availability(Guid contractId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? durationDays,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (from is not { } first || to is not { } last) return Invalid("from", "'from' and 'to' (yyyy-MM-dd) are required.");
        return Respond(await schedules.AvailabilityAsync(user, contractId, first, last, durationDays ?? 1, cancellationToken));
    }

    /// <summary>The contract's offers (open, selected and withdrawn, as history) with their current availability.</summary>
    [HttpGet("contracts/{contractId:guid}/installation-slot-offers")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Offers(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.ListOffersForTechnicianAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Retired (2026-11-18): the customer no longer chooses offered times in the app; agree the date in person and record it.</summary>
    [HttpPost("contracts/{contractId:guid}/installation-slot-offers")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Offer(Guid contractId) => RetiredOnSite("POST /api/technicians/me/contracts/{contractId}/installation-schedule (date agreed on site)");
}

/// <summary>
/// The customer's installation schedules (read): PLANNED (agreed with the technician, not confirmed) vs CONFIRMED. The customer does not select or confirm
/// anything; asking for another date (optional, never blocking) stays available.
/// </summary>
[ApiController]
[Route("api/customers/me")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerInstallationSchedulesController(InstallationScheduleHandlers schedules) : OperationResultControllerBase
{
    [HttpGet("installation-schedules")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Ok(ApiResponse<IReadOnlyList<InstallationScheduleView>>.Success(await schedules.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId()))
            : UnauthorizedFailure();

    [HttpGet("contracts/{contractId:guid}/installation-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.GetForCustomerAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("installation-schedules/{scheduleId:guid}/change-request")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> RequestChange(Guid scheduleId, [FromBody] InstallationScheduleChangeRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.RequestChangeAsync(user, scheduleId, request?.Note, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Retired (2026-11-18): the technician agrees the date with the customer in person; follow <c>…/installation-schedule</c>.</summary>
    [HttpGet("contracts/{contractId:guid}/installation-slot-offers")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Offers(Guid contractId) => RetiredOnSite("GET /api/customers/me/contracts/{contractId}/installation-schedule");

    /// <summary>Retired (2026-11-18): the customer does not select a time in the app.</summary>
    [HttpPost("installation-slot-offers/{offerId:guid}/select")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Select(Guid offerId) => RetiredOnSite("GET /api/customers/me/contracts/{contractId}/installation-schedule");
}

/// <summary>Manager/Admin supervision of technician-agreed schedules and exception handling.</summary>
[ApiController]
[Route("api/manager")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerInstallationSchedulesController(InstallationScheduleHandlers schedules) : OperationResultControllerBase
{
    [HttpGet("installation-schedules")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        var filter = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        return paging.Value is { } p
            ? Ok(ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<InstallationScheduleView>>.Success(await schedules.PageForStaffAsync(filter, p, cancellationToken), HttpContext.GetTraceId()))
            : Ok(ApiResponse<IReadOnlyList<InstallationScheduleView>>.Success(await schedules.ListForStaffAsync(filter, cancellationToken), HttpContext.GetTraceId()));
    }

    [HttpGet("contracts/{contractId:guid}/installation-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid contractId, CancellationToken cancellationToken) => Respond(await schedules.GetForStaffAsync(contractId, cancellationToken));

    /// <summary>Records a schedule for a named technician (exception: the surveying technician is unavailable). Same checks as the technician's.</summary>
    [HttpPost("contracts/{contractId:guid}/installation-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Propose(Guid contractId, [FromBody] InstallationScheduleRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.StartsAt is not { } start || request.EndsAt is not { } end) return Invalid("startsAt", "'startsAt' and 'endsAt' (ISO-8601 with offset) are required.");
        var result = await schedules.ProposeAsync(user, true, contractId,
            new ProposeInstallationScheduleInput(request.TechnicianUserId, start, end, request.CustomerAgreementNote, request.Reason, request.ExpectedRevision), cancellationToken);
        return Respond(result);
    }

    /// <summary>Retries the confirmation of a PAID contract's PLANNED schedule (idempotent; the sweep does the same every minute).</summary>
    [HttpPost("contracts/{contractId:guid}/installation-schedule/confirm")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Confirm(Guid contractId, CancellationToken cancellationToken)
        => Respond(await schedules.ConfirmForContractAsync(contractId, cancellationToken));

    [HttpPost("installation-schedules/{scheduleId:guid}/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Cancel(Guid scheduleId, [FromBody] InstallationScheduleReasonRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.CancelAsync(user, true, scheduleId, request?.Reason, cancellationToken)) : UnauthorizedFailure();
}
