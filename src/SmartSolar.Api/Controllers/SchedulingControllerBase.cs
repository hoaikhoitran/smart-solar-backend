using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
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
        SchedulingErrorCodes.ValidationFailed or SchedulingErrorCodes.InvalidBookingTime or SchedulingErrorCodes.AssignmentReasonRequired
            or DispatchErrorCodes.ReasonRequired => StatusCodes.Status400BadRequest,
        PreSurveyErrorCodes.SurveyRequestNotAssigned or SchedulingErrorCodes.SurveyRequestNotOwned
            or SchedulingErrorCodes.ZoneManagementForbidden or SchedulingErrorCodes.ZoneScopeForbidden => StatusCodes.Status403Forbidden,
        SchedulingErrorCodes.SurveyRequestNotFound or SchedulingErrorCodes.PropertySiteNotFound or SchedulingErrorCodes.BookingNotFound
            or SchedulingErrorCodes.AppointmentNotFound or SchedulingErrorCodes.TechnicianNotFound or SchedulingErrorCodes.ZoneNotFound
            or DispatchErrorCodes.DispatchRequestNotFound or DispatchErrorCodes.ProposalNotFound
            => StatusCodes.Status404NotFound,
        // Invalid or out-of-scope location (or the request names something unusable). A temporarily uncovered area — NO_ZONE_COVERAGE,
        // ZONE_NOT_SERVICEABLE — is NOT a 422: like the capacity codes it is a 409 with details.dispatchAvailable = true.
        SchedulingErrorCodes.SiteLocationRequired
            or SchedulingErrorCodes.LocationUnknown or SchedulingErrorCodes.LocationSuperseded or SchedulingErrorCodes.LocationOutOfServiceArea
            or SchedulingErrorCodes.ResponsibleManagerInvalid or SchedulingErrorCodes.SalesUserInvalid => StatusCodes.Status422UnprocessableEntity,
        // Slot taken, state, staleness, duplicates, eligibility (TECHNICIAN_*, OUTSIDE_WORKING_HOURS, DAILY_LIMIT_REACHED), already taken,
        // ZONE_* / COMMUNE_* conflicts (ZONE_CODE_EXISTS, ZONE_STALE_REVISION, ZONE_IN_USE, COMMUNE_ALREADY_MAPPED, COMMUNE_IN_USE),
        // coverage/capacity codes (NO_ZONE_COVERAGE, ZONE_NOT_SERVICEABLE, NO_ZONE_TECHNICIAN, NO_AVAILABLE_TECHNICIAN, SCHEDULE_CONFLICT),
        // zone authorization (ZONE_MANAGEMENT_FORBIDDEN, ZONE_SCOPE_FORBIDDEN are 403 above) and dispatch conflicts (DISPATCH_*, PROPOSAL_*,
        // CROSS_ZONE_AUTHORIZATION_REQUIRED).
        _ => StatusCodes.Status409Conflict,
    };
}
