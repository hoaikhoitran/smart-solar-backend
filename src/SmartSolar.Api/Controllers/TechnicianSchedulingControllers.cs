using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Api.Controllers;

/// <summary>Sales owner or Manager/Admin: technicians who can take a request's appointment, with reason codes for the others.</summary>
[ApiController]
[Route("api/technicians/availability")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
public sealed class TechnicianAvailabilityController(TechnicianAvailabilityHandler handler) : SchedulingControllerBase
{
    /// <summary>GET /api/technicians/availability?surveyRequestId=… (for the booked appointment interval).</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get([FromQuery] Guid surveyRequestId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (surveyRequestId == Guid.Empty) return InvalidField("surveyRequestId", "'surveyRequestId' is required.");
        var isManager = User.IsInRole(RoleCodes.Manager) || User.IsInRole(RoleCodes.Admin);
        return Respond(await handler.HandleAsync(new TechnicianAvailabilityQuery(userId, isManager, surveyRequestId), cancellationToken));
    }
}

/// <summary>
/// Technician: open jobs in their operational zones (self-acceptance) and their confirmed jobs. The V1
/// acknowledge endpoint was removed: acceptance itself confirms the job.
/// </summary>
[ApiController]
[Route("api/technicians/me")]
[Authorize(Roles = RoleCodes.Technician)]
public sealed class TechnicianAppointmentsController(TechnicianJobHandlers jobs, TechnicianScheduleHandler schedules, WorkingCalendar calendar, TimeProvider time)
    : SchedulingControllerBase
{
    [HttpGet("appointments")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Ok(await jobs.ListAsync(userId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("appointments/{appointmentId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await jobs.GetAsync(userId, appointmentId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Open (PENDING_ACCEPTANCE / ESCALATED) future appointments in the caller's active operational zones.</summary>
    [HttpGet("open-appointments")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> ListOpen(CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Ok(await jobs.ListOpenAsync(userId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>One open appointment; 404 when it is outside the caller's areas or no longer open.</summary>
    [HttpGet("open-appointments/{appointmentId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> GetOpen(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await jobs.GetOpenAsync(userId, appointmentId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Self-acceptance. 200 (also for a repeat by the same technician); 409 APPOINTMENT_ALREADY_TAKEN when someone else won.</summary>
    [HttpPost("appointments/{appointmentId:guid}/accept")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Accept(Guid appointmentId, [FromBody] AcceptAppointmentRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await jobs.AcceptAsync(new AcceptAppointmentCommand(userId, appointmentId, request?.ExpectedRevision), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>The confirmed technician can no longer attend (before the start; reason required).</summary>
    [HttpPost("appointments/{appointmentId:guid}/release")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Release(Guid appointmentId, [FromBody] ReleaseAppointmentRequest? request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (string.IsNullOrWhiteSpace(request?.Reason) || request.Reason.Length > 500) return InvalidField("reason", "'reason' is required (at most 500 characters).");
        return Respond(await jobs.ReleaseAsync(new ReleaseAppointmentCommand(userId, appointmentId, request.Reason), cancellationToken));
    }

    /// <summary>The technician's own day plan (confirmed jobs).</summary>
    [HttpGet("schedule")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Schedule([FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Ok(await schedules.HandleAsync(userId, date ?? calendar.LocalDateOf(time.GetUtcNow()), includeHolds: false, cancellationToken))
            : UnauthorizedFailure();
}

/// <summary>Dashboard data: any technician's day plan including internal capacity holds (MANAGER/ADMIN).</summary>
[ApiController]
[Route("api/technicians/{technicianUserId:guid}/schedule")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
public sealed class TechnicianScheduleController(TechnicianScheduleHandler schedules, WorkingCalendar calendar, TimeProvider time) : SchedulingControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get(Guid technicianUserId, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => Ok(await schedules.HandleAsync(technicianUserId, date ?? calendar.LocalDateOf(time.GetUtcNow()), includeHolds: true, cancellationToken));
}

/// <summary>Technician scheduling setup: profile, service areas, working schedule, leave (ADMIN/MANAGER).</summary>
[ApiController]
[Route("api/admin/technicians")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminTechniciansController(
    AdminTechnicianHandlers handlers,
    FluentValidation.IValidator<UpsertTechnicianProfileCommand> profileValidator,
    FluentValidation.IValidator<AddUnavailablePeriodCommand> periodValidator) : SchedulingControllerBase
{
    /// <summary>GET /api/admin/technicians[?zoneId=…] (zoneId: only the active members of that operational zone)</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> List([FromQuery] Guid? zoneId, CancellationToken cancellationToken) => Ok(await handlers.ListAsync(zoneId, cancellationToken));

    /// <summary>Scheduling flag and daily cap. Disabling is refused while confirmed appointments exist; pending holds move automatically.</summary>
    [HttpPut("{technicianUserId:guid}/profile")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> UpsertProfile(Guid technicianUserId, [FromBody] UpsertTechnicianProfileRequest request, CancellationToken cancellationToken)
    {
        var command = new UpsertTechnicianProfileCommand(technicianUserId, request.IsSchedulingActive, request.MaxAppointmentsPerDay, CurrentUserId());
        var validation = await profileValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.UpsertProfileAsync(command, cancellationToken));
    }

    /// <summary>
    /// Replaces the technician's operational zones (others are soft-removed, history kept). 409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS when
    /// confirmed future work would leave the technician's zones; pending holds are re-homed or escalated.
    /// </summary>
    [HttpPut("{technicianUserId:guid}/zones")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> SetZones(Guid technicianUserId, [FromBody] SetTechnicianZonesRequest request,
        [FromServices] FluentValidation.IValidator<SetTechnicianZonesCommand> validator, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new SetTechnicianZonesCommand(userId, technicianUserId, request.ZoneIds ?? []);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.SetZonesAsync(command, cancellationToken));
    }

    [HttpPost("{technicianUserId:guid}/unavailable-periods")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> AddUnavailablePeriod(Guid technicianUserId, [FromBody] AddUnavailablePeriodRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (!IsoTimestamp.TryParse(request.StartsAt, out var start)) return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset.");
        if (!IsoTimestamp.TryParse(request.EndsAt, out var end)) return InvalidField("endsAt", "'endsAt' must be ISO 8601 with an offset.");

        var command = new AddUnavailablePeriodCommand(userId, technicianUserId, start, end, request.Reason);
        var validation = await periodValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.AddUnavailablePeriodAsync(command, cancellationToken), StatusCodes.Status201Created);
    }

    [HttpGet("{technicianUserId:guid}/unavailable-periods")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> ListUnavailablePeriods(Guid technicianUserId, CancellationToken cancellationToken)
        => Ok(await handlers.ListUnavailablePeriodsAsync(technicianUserId, cancellationToken));

    [HttpGet("{technicianUserId:guid}/working-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> GetWorkingSchedule(Guid technicianUserId, CancellationToken cancellationToken)
        => Respond(await handlers.GetWorkingScheduleAsync(technicianUserId, cancellationToken));

    /// <summary>Replaces the technician's weekly schedule (local time). Refused with 409 when confirmed appointments would fall outside it.</summary>
    [HttpPut("{technicianUserId:guid}/working-schedule")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> SetWorkingSchedule(
        Guid technicianUserId, [FromBody] SetWorkingScheduleRequest request,
        [FromServices] FluentValidation.IValidator<SetWorkingScheduleCommand> validator, CancellationToken cancellationToken)
    {
        var periods = new List<WorkingPeriodInput>();
        foreach (var (p, i) in (request.Periods ?? []).Select((p, i) => (p, i)))
        {
            if (!Enum.TryParse<DayOfWeek>(p.Day?.Trim(), ignoreCase: true, out var day) || int.TryParse(p.Day, out _))
                return InvalidField($"periods[{i}].day", "'day' must be MONDAY … SUNDAY.");
            if (!TimeOnly.TryParseExact(p.Start?.Trim(), "HH:mm", out var start) || !TimeOnly.TryParseExact(p.End?.Trim(), "HH:mm", out var end))
                return InvalidField($"periods[{i}]", "'start' and 'end' must be local times in HH:mm format.");
            periods.Add(new WorkingPeriodInput(day, start, end));
        }

        var command = new SetWorkingScheduleCommand(technicianUserId, request.UseDefault, periods, CurrentUserId());
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.SetWorkingScheduleAsync(command, cancellationToken));
    }

    [HttpDelete("{technicianUserId:guid}/unavailable-periods/{periodId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> RemoveUnavailablePeriod(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => await handlers.RemoveUnavailablePeriodAsync(technicianUserId, periodId, cancellationToken)
            ? NoContent()
            : NotFound(Failure(Modules.Scheduling.Constants.SchedulingErrorCodes.TechnicianNotFound, "Unavailable period was not found."));
}
