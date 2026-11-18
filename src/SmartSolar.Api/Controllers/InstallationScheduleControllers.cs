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
/// The surveying technician records the installation date agreed with the customer after the contract is SIGNED (PLANNED; confirmed by the
/// system once the contract is PAID). Only contracts whose on-site survey the caller completed are visible (others: 404).
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

    /// <summary>Offers 1–5 intervals for the customer to choose (replaces the previous open offers; the customer is notified). 200 with the open offers.</summary>
    [HttpPost("contracts/{contractId:guid}/installation-slot-offers")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Offer(Guid contractId, [FromBody] InstallationSlotOffersRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Slots is not { Count: > 0 } slots || slots.Any(s => s.StartsAt is null || s.EndsAt is null))
            return Invalid("slots", "Give 1 to 5 slots, each with 'startsAt' and 'endsAt' (ISO-8601 with offset).");
        return Respond(await schedules.PublishOffersAsync(user, contractId, slots.Select(s => new SlotInterval(s.StartsAt!.Value, s.EndsAt!.Value)).ToList(),
            request.Note, cancellationToken));
    }
}

/// <summary>The customer's installation schedules: PLANNED (agreed, not confirmed) vs CONFIRMED, with the right to ask for another date.</summary>
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

    /// <summary>The installation times the technician offered for this contract (OPEN only), each re-checked now (<c>available</c>).</summary>
    [HttpGet("contracts/{contractId:guid}/installation-slot-offers")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Offers(Guid contractId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.ListOffersForCustomerAsync(user, contractId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>
    /// Chooses one offered time: re-validated now, then recorded as the PLANNED schedule (CONFIRMED at once when the contract is PAID).
    /// 409 <c>INSTALLATION_SLOT_OFFER_UNAVAILABLE</c> / <c>…_NOT_OPEN</c> when it is no longer possible; repeating the same choice returns it.
    /// </summary>
    [HttpPost("installation-slot-offers/{offerId:guid}/select")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Select(Guid offerId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await schedules.SelectOfferAsync(user, offerId, cancellationToken)) : UnauthorizedFailure();
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
