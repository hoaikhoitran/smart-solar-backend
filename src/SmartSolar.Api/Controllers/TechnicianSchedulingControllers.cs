using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Api.Controllers;

/// <summary>Sales: technicians who can actually take a claimed request's appointment.</summary>
[ApiController]
[Route("api/technicians/availability")]
[Authorize(Roles = RoleCodes.Sales)]
public sealed class TechnicianAvailabilityController(TechnicianAvailabilityHandler handler) : SchedulingControllerBase
{
    /// <summary>GET /api/technicians/availability?surveyRequestId=…[&amp;startsAt=…&amp;endsAt=…] (defaults to the booked time).</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get([FromQuery] Guid surveyRequestId, [FromQuery] string? startsAt, [FromQuery] string? endsAt, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (surveyRequestId == Guid.Empty) return InvalidField("surveyRequestId", "'surveyRequestId' is required.");

        DateTimeOffset? start = null, end = null;
        if (startsAt is not null)
        {
            if (!IsoTimestamp.TryParse(startsAt, out var s)) return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset.");
            start = s;
        }
        if (endsAt is not null)
        {
            if (!IsoTimestamp.TryParse(endsAt, out var e)) return InvalidField("endsAt", "'endsAt' must be ISO 8601 with an offset.");
            end = e;
        }
        if (end is not null && start is null) return InvalidField("startsAt", "'startsAt' is required when 'endsAt' is given.");

        return Respond(await handler.HandleAsync(new TechnicianAvailabilityQuery(userId, surveyRequestId, start, end), cancellationToken));
    }
}

/// <summary>Technician: officially assigned jobs only. Flow 1 ends with the acknowledgment.</summary>
[ApiController]
[Route("api/technicians/me")]
[Authorize(Roles = RoleCodes.Technician)]
public sealed class TechnicianAppointmentsController(TechnicianJobHandlers jobs, TechnicianScheduleHandler schedules, WorkingCalendar calendar, TimeProvider time)
    : SchedulingControllerBase
{
    [HttpGet("appointments")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Ok(await jobs.ListAsync(userId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("appointments/{appointmentId:guid}")]
    public async Task<IActionResult> Get(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await jobs.GetAsync(userId, appointmentId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Records receipt of the assigned job (idempotent). This is the final step of Flow 1.</summary>
    [HttpPost("appointments/{appointmentId:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await jobs.AcknowledgeAsync(userId, appointmentId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>The technician's own day plan with stored travel estimates.</summary>
    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule([FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Ok(await schedules.HandleAsync(userId, date ?? calendar.LocalDateOf(time.GetUtcNow()), cancellationToken))
            : UnauthorizedFailure();
}

/// <summary>Dashboard data: any technician's day plan (MANAGER/ADMIN).</summary>
[ApiController]
[Route("api/technicians/{technicianUserId:guid}/schedule")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
public sealed class TechnicianScheduleController(TechnicianScheduleHandler schedules, WorkingCalendar calendar, TimeProvider time) : SchedulingControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid technicianUserId, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => Ok(await schedules.HandleAsync(technicianUserId, date ?? calendar.LocalDateOf(time.GetUtcNow()), cancellationToken));
}

/// <summary>Minimal technician scheduling setup: base location, active flag, leave (ADMIN/MANAGER).</summary>
[ApiController]
[Route("api/admin/technicians")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminTechniciansController(
    AdminTechnicianHandlers handlers,
    FluentValidation.IValidator<UpsertTechnicianProfileCommand> profileValidator,
    FluentValidation.IValidator<AddUnavailablePeriodCommand> periodValidator) : SchedulingControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await handlers.ListAsync(cancellationToken));

    [HttpPut("{technicianUserId:guid}/profile")]
    public async Task<IActionResult> UpsertProfile(Guid technicianUserId, [FromBody] UpsertTechnicianProfileRequest request, CancellationToken cancellationToken)
    {
        TimeOnly? earliest = null;
        if (!string.IsNullOrWhiteSpace(request.EarliestBaseDeparture))
        {
            if (!TimeOnly.TryParseExact(request.EarliestBaseDeparture.Trim(), "HH:mm", out var parsed))
                return InvalidField("earliestBaseDeparture", "'earliestBaseDeparture' must be a local time in HH:mm format.");
            earliest = parsed;
        }

        var command = new UpsertTechnicianProfileCommand(technicianUserId, request.BaseLatitude, request.BaseLongitude, request.BaseLabel,
            request.IsSchedulingActive, request.MaxAppointmentsPerDay, earliest);
        var validation = await profileValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.UpsertProfileAsync(command, cancellationToken));
    }

    [HttpPost("{technicianUserId:guid}/unavailable-periods")]
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
    public async Task<IActionResult> ListUnavailablePeriods(Guid technicianUserId, CancellationToken cancellationToken)
        => Ok(await handlers.ListUnavailablePeriodsAsync(technicianUserId, cancellationToken));

    [HttpGet("{technicianUserId:guid}/working-schedule")]
    public async Task<IActionResult> GetWorkingSchedule(Guid technicianUserId, CancellationToken cancellationToken)
        => Respond(await handlers.GetWorkingScheduleAsync(technicianUserId, cancellationToken));

    /// <summary>Replaces the technician's weekly schedule (local time). Refused with 409 when existing appointments would fall outside it.</summary>
    [HttpPut("{technicianUserId:guid}/working-schedule")]
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

        var command = new SetWorkingScheduleCommand(technicianUserId, request.UseDefault, periods);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.SetWorkingScheduleAsync(command, cancellationToken));
    }

    [HttpDelete("{technicianUserId:guid}/unavailable-periods/{periodId:guid}")]
    public async Task<IActionResult> RemoveUnavailablePeriod(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => await handlers.RemoveUnavailablePeriodAsync(technicianUserId, periodId, cancellationToken)
            ? NoContent()
            : NotFound(Failure(Modules.Scheduling.Constants.SchedulingErrorCodes.TechnicianNotFound, "Unavailable period was not found."));
}
