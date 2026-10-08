using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Simulations;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Simulation snapshots of a pre-survey. Only the owning customer creates them (Draft only);
/// the owner and the assigned Sales user can read them. Reads never call external providers.
/// </summary>
[ApiController]
[Route("api/pre-surveys/{preSurveyId:guid}/simulations")]
[Authorize(Roles = $"{RoleCodes.Customer},{RoleCodes.Sales}")]
public sealed class SimulationsController : ControllerBase
{
    private readonly CreateSimulationHandler _createHandler;
    private readonly IValidator<CreateSimulationCommand> _createValidator;
    private readonly GetSimulationHandler _getHandler;
    private readonly ListSimulationsHandler _listHandler;

    public SimulationsController(
        CreateSimulationHandler createHandler,
        IValidator<CreateSimulationCommand> createValidator,
        GetSimulationHandler getHandler,
        ListSimulationsHandler listHandler)
    {
        _createHandler = createHandler;
        _createValidator = createValidator;
        _getHandler = getHandler;
        _listHandler = listHandler;
    }

    [HttpPost]
    [Authorize(Roles = RoleCodes.Customer)]
    [EnableRateLimiting(RateLimitingExtensions.SimulationWritePolicy)]
    public async Task<IActionResult> Create(Guid preSurveyId, [FromBody] CreateSimulationRequest request, CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return UnauthorizedFailure();
        }

        var spacing = request.Installation;
        var command = new CreateSimulationCommand(
            userId,
            preSurveyId,
            request.ExpectedGeometryVersion,
            request.ProductId,
            request.MountingType?.Trim().ToUpperInvariant(),
            request.PanelTiltDegree,
            request.PanelAzimuthDegree,
            new InstallationRequest(spacing?.PanelGapMm, spacing?.RowGapMm, spacing?.EdgeSetbackMm, spacing?.ObstacleClearanceMm, spacing?.RackLowEdgeClearanceMm),
            request.SystemLossPercent);

        var validation = await _createValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray());
            return BadRequest(Failure(SimulationErrorCodes.ValidationFailed, "One or more fields are invalid.", details));
        }

        var result = await _createHandler.HandleAsync(command, cancellationToken);

        return result.Outcome switch
        {
            CreateSimulationOutcome.Created => StatusCode(StatusCodes.Status201Created, Success(result.Simulation!)),
            CreateSimulationOutcome.Reused => Ok(Success(result.Simulation!)),
            CreateSimulationOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
            CreateSimulationOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, Failure(PreSurveyErrorCodes.PreSurveyNotOwned, "You do not have access to this pre-survey.")),
            CreateSimulationOutcome.NotEditable => Conflict(Failure(PreSurveyErrorCodes.PreSurveyNotEditable, "Simulations can only be created while the pre-survey is a draft.")),
            CreateSimulationOutcome.SourceChanged => Conflict(Failure(SimulationErrorCodes.SourceChanged, "The surface changed. Reload it and request the simulation again.")),
            CreateSimulationOutcome.SurfaceNotDefined => UnprocessableEntity(Failure(SimulationErrorCodes.SurfaceNotDefined, "Save the installation surface before requesting a simulation.")),
            CreateSimulationOutcome.ProductNotFound => NotFound(Failure(SimulationErrorCodes.ProductNotFound, "The product was not found or is not active.")),
            _ when result.ErrorCode == SimulationErrorCodes.ValidationFailed
                => BadRequest(Failure(SimulationErrorCodes.ValidationFailed, "One or more fields are invalid.", result.Problems)),
            _ => UnprocessableEntity(Failure(result.ErrorCode ?? SimulationErrorCodes.ValidationFailed,
                result.Problems.FirstOrDefault()?.Message ?? "The simulation request cannot be processed.", result.Problems)),
        };
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid preSurveyId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer)
        {
            return UnauthorizedFailure();
        }

        var result = await _listHandler.HandleAsync(viewer, preSurveyId, cancellationToken);
        return result.Outcome == SimulationQueryOutcome.Found ? Ok(Success(result.Simulations!)) : QueryFailure(result.Outcome);
    }

    [HttpGet("{simulationId:guid}")]
    public async Task<IActionResult> Get(Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer)
        {
            return UnauthorizedFailure();
        }

        var result = await _getHandler.HandleAsync(viewer, preSurveyId, simulationId, cancellationToken);
        return result.Outcome == SimulationQueryOutcome.Found ? Ok(Success(result.Simulation!)) : QueryFailure(result.Outcome);
    }

    private IActionResult QueryFailure(SimulationQueryOutcome outcome) => outcome switch
    {
        SimulationQueryOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
        SimulationQueryOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, Failure(SimulationErrorCodes.AccessDenied, "You do not have access to these simulations.")),
        _ => NotFound(Failure(SimulationErrorCodes.SimulationNotFound, "Simulation was not found.")),
    };

    private SimulationViewer? Viewer()
        => GetAuthenticatedUserId() is { } userId
            ? new SimulationViewer(userId, User.IsInRole(RoleCodes.Customer), User.IsInRole(RoleCodes.Sales))
            : null;

    private IActionResult UnauthorizedFailure()
        => Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));

    private Guid? GetAuthenticatedUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId) ? userId : null;
    }

    private ApiResponse<T> Success<T>(T data) => ApiResponse<T>.Success(data, HttpContext.GetTraceId());

    private ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());
}
