using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.PreSurveys;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.CreatePreSurvey;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

namespace SmartSolar.Api.Controllers
{
    [Route("api/pre-surveys")]
    [ApiController]
    [Authorize(Roles = RoleCodes.Customer)]
    public sealed class PreSurveysController : ControllerBase
    {
    private readonly CreatePreSurveyHandler _createHandler;
    private readonly IValidator<CreatePreSurveyCommand> _createValidator;
    private readonly SubmitPreSurveyHandler _submitHandler;
    private readonly IValidator<SubmitPreSurveyCommand> _submitValidator;
    private readonly UpdatePreSurveyHandler _updateHandler;
    private readonly IValidator<UpdatePreSurveyCommand> _updateValidator;
    public PreSurveysController(
        CreatePreSurveyHandler createHandler,
        IValidator<CreatePreSurveyCommand> createValidator,
        UpdatePreSurveyHandler updateHandler,
        IValidator<UpdatePreSurveyCommand> updateValidator,
        SubmitPreSurveyHandler submitHandler,
        IValidator<SubmitPreSurveyCommand> submitValidator)
    {
        _createHandler = createHandler;
        _createValidator = createValidator;
        _updateHandler = updateHandler;
        _updateValidator = updateValidator;
        _submitHandler = submitHandler;
        _submitValidator = submitValidator;
    }
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreatePreSurveyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();

        if (userId is null)
        {
            return Unauthorized(
                Failure(
                    PreSurveyErrorCodes.Unauthorized,
                    "Authentication is required."));
        }

        var command = new CreatePreSurveyCommand(
            userId.Value,
            request.PropertySiteId,
            request.TotalAreaM2,
            request.UsableAreaM2,
            request.TiltDegree,
            request.AzimuthDegree,
            request.HasObstruction);

        var validation =
            await _createValidator.ValidateAsync(
                command,
                cancellationToken);

        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(x => x.ErrorMessage)
                        .ToArray());

            return BadRequest(
                Failure(
                    PreSurveyErrorCodes.ValidationFailed,
                    "One or more fields are invalid.",
                    details));
        }

        var result =
            await _createHandler.HandleAsync(
                command,
                cancellationToken);

        return result.Outcome switch
        {
            CreatePreSurveyOutcome.CustomerNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.CustomerProfileNotFound,
                        "Customer profile was not found.")),

            CreatePreSurveyOutcome.PropertySiteNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.PropertySiteNotFound,
                        "Property site was not found.")),

            CreatePreSurveyOutcome.PropertySiteNotOwned
                => StatusCode(
                    StatusCodes.Status403Forbidden,
                    Failure(
                        PreSurveyErrorCodes.PropertySiteNotOwned,
                        "You do not have access to this property site.")),

            _ => StatusCode(
                StatusCodes.Status201Created,
                Success(
                    new CreatePreSurveyResponse(
                        result.PreSurveyId!.Value)))
        };
    }
    
    [HttpPost("{preSurveyId:guid}/submit")]
    public async Task<IActionResult> Submit(
        Guid preSurveyId,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();

        if (userId is null)
        {
            return Unauthorized(
                Failure(
                    PreSurveyErrorCodes.Unauthorized,
                    "Authentication is required."));
        }

        var command = new SubmitPreSurveyCommand(
            userId.Value,
            preSurveyId);

        var validation =
            await _submitValidator.ValidateAsync(
                command,
                cancellationToken);

        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(x => x.ErrorMessage)
                        .ToArray());

            return BadRequest(
                Failure(
                    PreSurveyErrorCodes.ValidationFailed,
                    "One or more fields are invalid.",
                    details));
        }

        var result =
            await _submitHandler.HandleAsync(
                command,
                cancellationToken);

        return result.Outcome switch
        {
            SubmitPreSurveyOutcome.CustomerNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.CustomerProfileNotFound,
                        "Customer profile was not found.")),

            SubmitPreSurveyOutcome.PreSurveyNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.PreSurveyNotFound,
                        "Pre-survey was not found.")),

            SubmitPreSurveyOutcome.NotOwned
                => StatusCode(
                    StatusCodes.Status403Forbidden,
                    Failure(
                        PreSurveyErrorCodes.PreSurveyNotOwned,
                        "You do not have access to this pre-survey.")),

            SubmitPreSurveyOutcome.AlreadySubmitted
                => Conflict(
                    Failure(
                        PreSurveyErrorCodes.PreSurveyAlreadySubmitted,
                        "Pre-survey has already been submitted.")),

            SubmitPreSurveyOutcome.Incomplete
                => BadRequest(
                    Failure(
                        PreSurveyErrorCodes.PreSurveyIncomplete,
                        "Pre-survey does not contain all required information.")),

            _ => Ok(
                Success(
                    new SubmitPreSurveyResponse(
                        result.SurveyRequestId!.Value)))
        };
    }

    [HttpPut("{preSurveyId:guid}")]
    public async Task<IActionResult> Update(
        Guid preSurveyId,
        [FromBody] UpdatePreSurveyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();

        if (userId is null)
        {
            return Unauthorized(
                Failure(
                    PreSurveyErrorCodes.Unauthorized,
                    "Authentication is required."));
        }

        var command = new UpdatePreSurveyCommand(
            userId.Value,
            preSurveyId,
            request.TotalAreaM2,
            request.UsableAreaM2,
            request.TiltDegree,
            request.AzimuthDegree,
            request.HasObstruction);

        var validation =
            await _updateValidator.ValidateAsync(
                command,
                cancellationToken);

        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(x => x.ErrorMessage)
                        .ToArray());

            return BadRequest(
                Failure(
                    PreSurveyErrorCodes.ValidationFailed,
                    "One or more fields are invalid.",
                    details));
        }

        var result =
            await _updateHandler.HandleAsync(
                command,
                cancellationToken);

        return result.Outcome switch
        {
            UpdatePreSurveyOutcome.CustomerNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.CustomerProfileNotFound,
                        "Customer profile was not found.")),

            UpdatePreSurveyOutcome.PreSurveyNotFound
                => NotFound(
                    Failure(
                        PreSurveyErrorCodes.PreSurveyNotFound,
                        "Pre-survey was not found.")),

            UpdatePreSurveyOutcome.NotOwned
                => StatusCode(
                    StatusCodes.Status403Forbidden,
                    Failure(
                        PreSurveyErrorCodes.PreSurveyNotOwned,
                        "You do not have access to this pre-survey.")),

            UpdatePreSurveyOutcome.NotEditable
                => Conflict(
                    Failure(
                        PreSurveyErrorCodes.PreSurveyNotEditable,
                        "Only draft pre-surveys can be edited.")),

            _ => NoContent()
        };
    }
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
    
}
