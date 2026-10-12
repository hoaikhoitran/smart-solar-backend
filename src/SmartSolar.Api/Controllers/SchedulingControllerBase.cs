using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Api.Controllers;

/// <summary>Shared envelope and error-code → HTTP status mapping for scheduling endpoints.</summary>
public abstract class SchedulingControllerBase : ControllerBase
{
    protected Guid? CurrentUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var id) ? id : null;
    }

    protected IActionResult UnauthorizedFailure() => Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));

    protected IActionResult Respond<T>(SchedulingResult<T> result, int successStatus = StatusCodes.Status200OK)
        => result.Succeeded
            ? StatusCode(successStatus, ApiResponse<T>.Success(result.Value!, HttpContext.GetTraceId()))
            : StatusCode(StatusFor(result.ErrorCode!), Failure(result.ErrorCode!, result.Message!, result.Details));

    protected IActionResult Ok<T>(T data) => base.Ok(ApiResponse<T>.Success(data, HttpContext.GetTraceId()));

    protected IActionResult ValidationFailure(ValidationResult validation)
        => BadRequest(Failure(SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.",
            validation.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())));

    protected IActionResult InvalidField(string field, string message)
        => BadRequest(Failure(SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] }));

    protected ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());

    public static int StatusFor(string code) => code switch
    {
        SchedulingErrorCodes.ValidationFailed or SchedulingErrorCodes.InvalidBookingTime => StatusCodes.Status400BadRequest,
        PreSurveyErrorCodes.SurveyRequestNotAssigned => StatusCodes.Status403Forbidden,
        SchedulingErrorCodes.SurveyRequestNotFound or SchedulingErrorCodes.PropertySiteNotFound or SchedulingErrorCodes.BookingNotFound
            or SchedulingErrorCodes.AppointmentNotFound or SchedulingErrorCodes.TechnicianNotFound => StatusCodes.Status404NotFound,
        SchedulingErrorCodes.SlotUnavailable or SchedulingErrorCodes.BookingAlreadyExists or SchedulingErrorCodes.InvalidBookingState
            or SchedulingErrorCodes.TechnicianNotAvailable or SchedulingErrorCodes.ConcurrencyConflict
            or SchedulingErrorCodes.TechnicianHasConflictingAppointments or SchedulingErrorCodes.TechnicianProfileRequired => StatusCodes.Status409Conflict,
        SchedulingErrorCodes.PropertySiteLocationRequired or SchedulingErrorCodes.TravelRouteUnavailable => StatusCodes.Status422UnprocessableEntity,
        SchedulingErrorCodes.TravelProviderUnavailable or SchedulingErrorCodes.TravelProviderQuotaExceeded => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
