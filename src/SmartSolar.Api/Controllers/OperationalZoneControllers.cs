using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Api.Controllers;

/// <summary>Operational zones (ADMIN/MANAGER): zones, their commune sets, their technicians and coverage gaps.</summary>
[ApiController]
[Route("api/admin/operational-zones")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminOperationalZonesController(ZoneHandlers handlers) : SchedulingControllerBase
{
    /// <summary>Every zone with commune count, member count, schedulable member count and hasAvailableTechnician.</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await handlers.ListAsync(cancellationToken));

    /// <summary>Active catalog communes without an active zone, and active zones without a schedulable technician.</summary>
    [HttpGet("coverage")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Coverage([FromQuery] string? provinceCode, CancellationToken cancellationToken)
        => Ok(await handlers.CoverageAsync(provinceCode, cancellationToken));

    [HttpGet("{zoneId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get(Guid zoneId, CancellationToken cancellationToken) => Respond(await handlers.GetAsync(zoneId, cancellationToken));

    [HttpPost]
    [Authorize(Roles = RoleCodes.Admin)]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Create([FromBody] CreateZoneRequest request, [FromServices] IValidator<CreateZoneCommand> validator,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new CreateZoneCommand(userId, request.Code, request.Name, request.IsActive, request.IsServiceable, request.ResponsibleManagerUserId);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.CreateAsync(command, cancellationToken), StatusCodes.Status201Created);
    }

    /// <summary>Name, active flag, serviceability and responsible manager (the code is immutable). Requires the current revision.</summary>
    [HttpPut("{zoneId:guid}")]
    [Authorize(Roles = RoleCodes.Admin)]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Update(Guid zoneId, [FromBody] UpdateZoneRequest request, [FromServices] IValidator<UpdateZoneCommand> validator,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new UpdateZoneCommand(userId, zoneId, request.Name, request.IsActive, request.IsServiceable, request.ResponsibleManagerUserId, request.Revision);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.UpdateAsync(command, cancellationToken));
    }

    /// <summary>Replaces the zone's commune set (exclusive mapping; history kept). Requires the current revision.</summary>
    [HttpPut("{zoneId:guid}/communes")]
    [Authorize(Roles = RoleCodes.Admin)]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> SetCommunes(Guid zoneId, [FromBody] SetZoneCommunesRequest request, [FromServices] IValidator<SetZoneCommunesCommand> validator,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new SetZoneCommunesCommand(userId, zoneId, request.CommuneCodes, request.Reason, request.Revision);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handlers.SetCommunesAsync(command, cancellationToken));
    }

    [HttpGet("{zoneId:guid}/technicians")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Technicians(Guid zoneId, CancellationToken cancellationToken)
        => Respond(await handlers.TechniciansAsync(zoneId, cancellationToken));
}

/// <summary>Manager/Admin override of a survey appointment's technician (bypasses Sales ownership, reason required).</summary>
[ApiController]
[Route("api/admin/survey-appointments")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminSurveyAppointmentsController(AssignTechnicianHandler handler, IValidator<OverrideTechnicianCommand> validator) : SchedulingControllerBase
{
    [HttpPost("{appointmentId:guid}/assign-technician")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Assign(Guid appointmentId, [FromBody] AssignTechnicianRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new OverrideTechnicianCommand(userId, appointmentId, request.TechnicianUserId, request.Reason, request.ExpectedRevision);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handler.OverrideAsync(command, cancellationToken));
    }
}

/// <summary>Manager/Admin: commercial (Sales) ownership of survey requests.</summary>
[ApiController]
[Route("api/admin/survey-requests")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminSurveyRequestsController(SalesOwnerHandler handler, IValidator<SetSalesOwnerCommand> validator) : SchedulingControllerBase
{
    [HttpPut("{surveyRequestId:guid}/sales-owner")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> SetSalesOwner(Guid surveyRequestId, [FromBody] SetSalesOwnerRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        var command = new SetSalesOwnerCommand(userId, surveyRequestId, request.SaleUserId, request.Reason);
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);
        return Respond(await handler.HandleAsync(command, cancellationToken));
    }
}

/// <summary>
/// The customer sets the official location (province + current commune) of their own site. Refused while a survey appointment
/// recording a zone is active (SITE_HAS_ACTIVE_BOOKING) or an installation project is unfinished (SITE_HAS_ACTIVE_INSTALLATION).
/// </summary>
[ApiController]
[Route("api/customers/me/sites/{siteId:guid}/administrative-location")]
[Authorize(Roles = RoleCodes.Customer)]
public sealed class CustomerSiteLocationController(ZoneHandlers handlers) : SchedulingControllerBase
{
    [HttpPut]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Set(Guid siteId, [FromBody] SetSiteAdministrativeLocationRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId
            ? Respond(await handlers.SetSiteLocationAsync(new SetSiteLocationCommand(userId, siteId, request.ProvinceCode, request.CommuneCode), cancellationToken))
            : UnauthorizedFailure();
}
