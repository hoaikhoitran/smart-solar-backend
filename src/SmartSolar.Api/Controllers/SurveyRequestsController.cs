using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.SurveyRequests;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Api.Contracts.Scheduling;
using FluentValidation;

namespace SmartSolar.Api.Controllers;

[ApiController]
[Route("api/survey-requests")]
[Authorize(Roles = RoleCodes.Sales)]
public sealed class SurveyRequestsController : ControllerBase
{
    private readonly GetPendingSurveyRequestsHandler
    _getPendingHandler;
    private readonly ClaimSurveyRequestHandler
    _claimHandler;
    private readonly GetMySurveyRequestsHandler _getMyHandler;
    private readonly GetSurveyRequestDetailHandler _getDetailHandler;

    public SurveyRequestsController(
    GetPendingSurveyRequestsHandler getPendingHandler,
    ClaimSurveyRequestHandler claimHandler,
    GetMySurveyRequestsHandler getMyHandler,
    GetSurveyRequestDetailHandler getDetailHandler)
    {
        _getPendingHandler = getPendingHandler;
        _claimHandler = claimHandler;
        _getMyHandler = getMyHandler;
        _getDetailHandler = getDetailHandler;
    }

    /// <summary>The unclaimed queue: non-cancelled requests without a commercial owner (whatever their scheduling state).</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(
        CancellationToken cancellationToken)
    {
        var requests =
            await _getPendingHandler.HandleAsync(
                cancellationToken);

        return Ok(Success(requests));
    }

    [HttpPost("{surveyRequestId:guid}/claim")]
    public async Task<IActionResult> Claim(
        Guid surveyRequestId,
        CancellationToken cancellationToken)
    {
        var saleUserId = GetAuthenticatedUserId();

        if (saleUserId is null)
        {
            return UnauthorizedFailure();
        }

        var command = new ClaimSurveyRequestCommand(
            surveyRequestId,
            saleUserId.Value);

        var result =
            await _claimHandler.HandleAsync(
                command,
                cancellationToken);

        if (result.Outcome ==
            ClaimSurveyRequestOutcome.Unavailable)
        {
            return Conflict(
                Failure(
                    PreSurveyErrorCodes.SurveyRequestUnavailable,
                    "Survey request is no longer available."));
        }

        return Ok(Success(new ClaimSurveyRequestResponse(surveyRequestId)));
    }

    [HttpGet("my")]
    public async Task<IActionResult> GetMy(
        CancellationToken cancellationToken)
    {
        var saleUserId = GetAuthenticatedUserId();

        if (saleUserId is null)
        {
            return UnauthorizedFailure();
        }

        var requests =
            await _getMyHandler.HandleAsync(
                saleUserId.Value,
                cancellationToken);

        return Ok(Success(requests));
    }

    [HttpGet("{surveyRequestId:guid}")]
    public async Task<IActionResult> GetDetail(
        Guid surveyRequestId,
        CancellationToken cancellationToken)
    {
        var saleUserId = GetAuthenticatedUserId();

        if (saleUserId is null)
        {
            return UnauthorizedFailure();
        }

        var result =
            await _getDetailHandler.HandleAsync(
                surveyRequestId,
                saleUserId.Value,
                cancellationToken);

        return result.Outcome switch
        {
            GetSurveyRequestDetailOutcome.NotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.SurveyRequestNotFound,
                        "Survey request was not found.")),

            GetSurveyRequestDetailOutcome.NotAssignedToSale
                => StatusCode(
                    StatusCodes.Status403Forbidden,
                    Failure(
                        PreSurveyErrorCodes.SurveyRequestNotAssigned,
                        "Survey request is not assigned to you.")),

            _ => Ok(Success(result.Detail!))
        };
    }

    /// <summary>
    /// Sales fallback: the commercial owner assigns a technician to the request's open or confirmed future
    /// appointment (reason required). 403 SURVEY_REQUEST_NOT_OWNED for anyone else; claim first.
    /// PUT …/reassign-technician is kept as an alias.
    /// </summary>
    [HttpPost("{surveyRequestId:guid}/assign-technician")]
    [HttpPut("{surveyRequestId:guid}/reassign-technician")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> AssignTechnician(
        Guid surveyRequestId,
        [FromBody] AssignTechnicianRequest request,
        [FromServices] AssignTechnicianHandler assignHandler,
        [FromServices] IValidator<AssignTechnicianCommand> assignValidator,
        CancellationToken cancellationToken)
    {
        var saleUserId = GetAuthenticatedUserId();

        if (saleUserId is null)
        {
            return UnauthorizedFailure();
        }

        var command = new AssignTechnicianCommand(saleUserId.Value, surveyRequestId, request.TechnicianUserId, request.Reason, request.ExpectedRevision);
        var validation = await assignValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(Failure(
                Modules.Scheduling.Constants.SchedulingErrorCodes.ValidationFailed,
                "One or more fields are invalid.",
                validation.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())));
        }

        var result = await assignHandler.HandleAsync(command, cancellationToken);
        return result.Succeeded
            ? Ok(Success(result.Value!))
            : StatusCode(SchedulingControllerBase.StatusFor(result.ErrorCode!), Failure(result.ErrorCode!, result.Message!, result.Details));
    }

    /// <summary>
    /// The staff queue: ESCALATED appointments and pending ones past their acceptance deadline (owned and unowned),
    /// with the service area and whether any technician could still take them.
    /// </summary>
    [HttpGet("escalated")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitingExtensions.SchedulingReadPolicy)]
    public async Task<IActionResult> GetEscalated(
        [FromServices] EscalatedAppointmentsHandler escalatedHandler,
        CancellationToken cancellationToken)
    {
        var saleUserId = GetAuthenticatedUserId();

        if (saleUserId is null)
        {
            return UnauthorizedFailure();
        }

        return Ok(Success(await escalatedHandler.HandleAsync(saleUserId.Value, cancellationToken)));
    }

    /// <summary>
    /// Sales reschedule of an owned request. The confirmed technician keeps the job when still eligible at the
    /// new time; pass technicianUserId (with a reason) to reschedule and assign together.
    /// </summary>
    [HttpPut("{surveyRequestId:guid}/reschedule")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(RateLimitingExtensions.SchedulingWritePolicy)]
    public async Task<IActionResult> Reschedule(
        Guid surveyRequestId,
        [FromBody] SalesRescheduleRequest request,
        [FromServices] SalesRescheduleHandler rescheduleHandler,
        CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } saleUserId)
        {
            return UnauthorizedFailure();
        }

        if (!IsoTimestamp.TryParse(request.StartsAt, out var startsAt))
        {
            return BadRequest(Failure(Modules.Scheduling.Constants.SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["startsAt"] = ["'startsAt' must be ISO 8601 with an offset, e.g. 2026-10-22T13:30:00+07:00."] }));
        }

        if (request.Reason is { Length: > 500 })
        {
            return BadRequest(Failure(Modules.Scheduling.Constants.SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["reason"] = ["'reason' must be at most 500 characters."] }));
        }

        var result = await rescheduleHandler.HandleAsync(
            new SalesRescheduleCommand(saleUserId, surveyRequestId, startsAt, request.Reason, request.TechnicianUserId), cancellationToken);
        return result.Succeeded
            ? Ok(Success(result.Value!))
            : StatusCode(SchedulingControllerBase.StatusFor(result.ErrorCode!), Failure(result.ErrorCode!, result.Message!, result.Details));
    }

    private IActionResult UnauthorizedFailure()
        => Unauthorized(
            Failure(
                PreSurveyErrorCodes.Unauthorized,
                "Authentication is required."));

    private Guid? GetAuthenticatedUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId) ? userId : null;
    }

    private ApiResponse<T> Success<T>(T data)
    => ApiResponse<T>.Success(
        data,
        HttpContext.GetTraceId());

    private ApiResponse<object> Failure(
    string code,
    string message,
    object? details = null)
    => ApiResponse<object>.Failure(
        new ApiError(code, message, details),
        HttpContext.GetTraceId());
}
