using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Scheduling;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Api.Controllers;

/// <summary>Dynamic survey appointment slots for the customer's own property site.</summary>
[ApiController]
[Route("api/survey-slots")]
[Authorize(Roles = RoleCodes.Customer)]
public sealed class SurveySlotsController(GetSurveySlotsHandler handler) : SchedulingControllerBase
{
    /// <summary>GET /api/survey-slots?propertySiteId={id}&amp;date=YYYY-MM-DD — only times with verified technician capacity.</summary>
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> Get([FromQuery] Guid propertySiteId, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (propertySiteId == Guid.Empty) return InvalidField("propertySiteId", "'propertySiteId' is required.");
        if (date is null) return InvalidField("date", "'date' is required (YYYY-MM-DD).");

        return Respond(await handler.HandleAsync(new GetSurveySlotsQuery(userId, propertySiteId, date.Value), cancellationToken));
    }
}

/// <summary>The customer's own survey requests and their appointment booking.</summary>
[ApiController]
[Route("api/customers/me/survey-requests")]
[Authorize(Roles = RoleCodes.Customer)]
public sealed class CustomerSurveyRequestsController(
    CustomerSurveyRequestQueries queries,
    BookSurveyAppointmentHandler bookHandler,
    IValidator<BookSurveyAppointmentCommand> bookValidator,
    RescheduleSurveyAppointmentHandler rescheduleHandler,
    IValidator<RescheduleSurveyAppointmentCommand> rescheduleValidator,
    CancelSurveyAppointmentHandler cancelHandler) : SchedulingControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Ok(await queries.ListAsync(userId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("{surveyRequestId:guid}")]
    public async Task<IActionResult> Get(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } userId ? Respond(await queries.GetAsync(userId, surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Books a survey appointment immediately (201). Retrying with the same start returns the existing booking (200).</summary>
    [HttpPost("{surveyRequestId:guid}/booking")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Book(Guid surveyRequestId, [FromBody] BookSurveyAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (!IsoTimestamp.TryParse(request.StartsAt, out var startsAt))
            return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset, e.g. 2026-10-20T08:00:00+07:00.");

        var command = new BookSurveyAppointmentCommand(userId, surveyRequestId, startsAt, request.Note);
        var validation = await bookValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);

        var result = await bookHandler.HandleAsync(command, cancellationToken);
        return Respond(result, result.Value is { Created: true } ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    /// <summary>Moves a not-yet-assigned future appointment to another start time (history kept).</summary>
    [HttpPut("{surveyRequestId:guid}/booking")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Reschedule(Guid surveyRequestId, [FromBody] BookSurveyAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (!IsoTimestamp.TryParse(request.StartsAt, out var startsAt))
            return InvalidField("startsAt", "'startsAt' must be ISO 8601 with an offset, e.g. 2026-10-20T08:00:00+07:00.");

        var command = new RescheduleSurveyAppointmentCommand(userId, surveyRequestId, startsAt, request.Note);
        var validation = await rescheduleValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid) return ValidationFailure(validation);

        return Respond(await rescheduleHandler.HandleAsync(command, cancellationToken));
    }

    /// <summary>Cancels a not-yet-assigned future appointment and releases the capacity (history kept).</summary>
    [HttpPost("{surveyRequestId:guid}/booking/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Cancel(Guid surveyRequestId, [FromBody] CancelSurveyAppointmentRequest? request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return UnauthorizedFailure();
        if (request?.Reason is { Length: > 200 }) return InvalidField("reason", "'reason' must be at most 200 characters.");
        return Respond(await cancelHandler.HandleAsync(new CancelSurveyAppointmentCommand(userId, surveyRequestId, request?.Reason), cancellationToken));
    }
}
