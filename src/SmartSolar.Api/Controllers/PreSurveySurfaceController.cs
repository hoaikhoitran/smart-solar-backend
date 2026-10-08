using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.PreSurveys;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.GetPreSurveySurface;
using SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;

namespace SmartSolar.Api.Controllers;

/// <summary>Rectangular installation surface and obstacles of a pre-survey (meters, surface frame).</summary>
[ApiController]
[Route("api/pre-surveys/{preSurveyId:guid}/surface")]
[Authorize(Roles = RoleCodes.Customer)]
public sealed class PreSurveySurfaceController : ControllerBase
{
    private readonly UpdatePreSurveySurfaceHandler _updateHandler;
    private readonly IValidator<UpdatePreSurveySurfaceCommand> _updateValidator;
    private readonly GetPreSurveySurfaceHandler _getHandler;

    public PreSurveySurfaceController(
        UpdatePreSurveySurfaceHandler updateHandler,
        IValidator<UpdatePreSurveySurfaceCommand> updateValidator,
        GetPreSurveySurfaceHandler getHandler)
    {
        _updateHandler = updateHandler;
        _updateValidator = updateValidator;
        _getHandler = getHandler;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid preSurveyId, CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));
        }

        var result = await _getHandler.HandleAsync(userId, preSurveyId, cancellationToken);

        return result.Outcome switch
        {
            GetPreSurveySurfaceOutcome.CustomerNotFound => NotFound(Failure(PreSurveyErrorCodes.CustomerProfileNotFound, "Customer profile was not found.")),
            GetPreSurveySurfaceOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
            GetPreSurveySurfaceOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, Failure(PreSurveyErrorCodes.PreSurveyNotOwned, "You do not have access to this pre-survey.")),
            _ => Ok(Success(result.Surface!)),
        };
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid preSurveyId, [FromBody] UpdatePreSurveySurfaceRequest request, CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));
        }

        var command = new UpdatePreSurveySurfaceCommand(
            userId,
            preSurveyId,
            request.ExpectedRevision,
            request.SurfaceLengthM,
            request.SurfaceWidthM,
            request.SurfaceTiltDegree,
            request.SurfaceAzimuthDegree,
            // Null items are passed through so the validator rejects them with a 400 envelope.
            request.Obstacles?.Select(o => o is null ? null! : new SurfaceObstacleInput(o.Name, o.XM, o.YM, o.WidthM, o.LengthM, o.HeightM)).ToList());

        var validation = await _updateValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray());
            return BadRequest(Failure(PreSurveyErrorCodes.ValidationFailed, "One or more fields are invalid.", details));
        }

        var result = await _updateHandler.HandleAsync(command, cancellationToken);

        return result.Outcome switch
        {
            UpdatePreSurveySurfaceOutcome.CustomerNotFound => NotFound(Failure(PreSurveyErrorCodes.CustomerProfileNotFound, "Customer profile was not found.")),
            UpdatePreSurveySurfaceOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
            UpdatePreSurveySurfaceOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, Failure(PreSurveyErrorCodes.PreSurveyNotOwned, "You do not have access to this pre-survey.")),
            UpdatePreSurveySurfaceOutcome.NotEditable => Conflict(Failure(PreSurveyErrorCodes.PreSurveyNotEditable, "Only draft pre-surveys can be edited.")),
            UpdatePreSurveySurfaceOutcome.ConcurrentlyModified => Conflict(Failure(PreSurveyErrorCodes.PreSurveyConcurrentlyModified, "Pre-survey was changed by another request. Reload and try again.")),
            _ => Ok(Success(new UpdatePreSurveySurfaceResponse(result.Revision!.Value, result.GeometryVersion!.Value))),
        };
    }

    private Guid? GetAuthenticatedUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId) ? userId : null;
    }

    private ApiResponse<T> Success<T>(T data) => ApiResponse<T>.Success(data, HttpContext.GetTraceId());

    private ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());
}
