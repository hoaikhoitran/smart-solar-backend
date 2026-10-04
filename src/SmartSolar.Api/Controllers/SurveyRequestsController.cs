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
