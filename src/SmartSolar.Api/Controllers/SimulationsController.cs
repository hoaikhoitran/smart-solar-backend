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
using SmartSolar.Modules.SolarSimulation.SelectSimulation;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Simulations of a pre-survey. Only the owning customer creates them (Draft only); results are
/// temporary (cached for SolarSimulation:ResultTtlMinutes) and nothing is selected automatically:
/// the customer selects one explicitly, which stores a compact summary (and optionally a durable
/// snapshot). The owner, the assigned Sales user and the assigned technician can read them.
/// Reads never call external providers. 410 SIMULATION_EXPIRED: the result left the cache and is not
/// the selection. 503 SIMULATION_CACHE_UNAVAILABLE: the cache could not be reached (not expiry).
/// </summary>
[ApiController]
[Route("api/pre-surveys/{preSurveyId:guid}/simulations")]
[Authorize(Roles = $"{RoleCodes.Customer},{RoleCodes.Sales},{RoleCodes.Technician}")]
public sealed class SimulationsController : ControllerBase
{
    private readonly CreateSimulationHandler _createHandler;
    private readonly IValidator<CreateSimulationCommand> _createValidator;
    private readonly GetSimulationHandler _getHandler;
    private readonly ListSimulationsHandler _listHandler;
    private readonly SelectSimulationHandler _selectHandler;
    private readonly IValidator<SelectSimulationCommand> _selectValidator;

    public SimulationsController(
        CreateSimulationHandler createHandler,
        IValidator<CreateSimulationCommand> createValidator,
        GetSimulationHandler getHandler,
        ListSimulationsHandler listHandler,
        SelectSimulationHandler selectHandler,
        IValidator<SelectSimulationCommand> selectValidator)
    {
        _createHandler = createHandler;
        _createValidator = createValidator;
        _getHandler = getHandler;
        _listHandler = listHandler;
        _selectHandler = selectHandler;
        _selectValidator = selectValidator;
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
            CreateSimulationOutcome.CacheUnavailable => CacheUnavailableFailure(),
            _ when result.ErrorCode == SimulationErrorCodes.ValidationFailed
                => BadRequest(Failure(SimulationErrorCodes.ValidationFailed, "One or more fields are invalid.", result.Problems)),
            _ => UnprocessableEntity(Failure(result.ErrorCode ?? SimulationErrorCodes.ValidationFailed,
                result.Problems.FirstOrDefault()?.Message ?? "The simulation request cannot be processed.", result.Problems)),
        };
    }

    /// <summary>Cached recent results (newest first) and the stored selected summary.</summary>
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

    /// <summary>
    /// The full result while cached (source CACHE); for the selected simulation after expiry, its
    /// verified durable snapshot (source SNAPSHOT) or its compact summary (source SUMMARY).
    /// </summary>
    [HttpGet("{simulationId:guid}")]
    public async Task<IActionResult> Get(Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer)
        {
            return UnauthorizedFailure();
        }

        var result = await _getHandler.HandleAsync(viewer, preSurveyId, simulationId, cancellationToken);
        return result.Outcome switch
        {
            SimulationQueryOutcome.Found => Ok(Success(result.Simulation!)),
            SimulationQueryOutcome.SummaryOnly => Ok(Success(result.Summary!)),
            _ => QueryFailure(result.Outcome),
        };
    }

    /// <summary>Selects a cached result (Draft only). Re-selecting replaces the previous selection.</summary>
    [HttpPost("{simulationId:guid}/select")]
    [Authorize(Roles = RoleCodes.Customer)]
    [EnableRateLimiting(RateLimitingExtensions.SimulationWritePolicy)]
    public async Task<IActionResult> Select(Guid preSurveyId, Guid simulationId, [FromBody] SelectSimulationRequest request, CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return UnauthorizedFailure();
        }

        var command = new SelectSimulationCommand(userId, preSurveyId, simulationId, request.ExpectedGeometryVersion);
        var validation = await _selectValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray());
            return BadRequest(Failure(SimulationErrorCodes.ValidationFailed, "One or more fields are invalid.", details));
        }

        var result = await _selectHandler.HandleAsync(command, cancellationToken);
        return result.Outcome switch
        {
            SelectSimulationResultOutcome.Selected => Ok(Success(result.Selection!)),
            SelectSimulationResultOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
            SelectSimulationResultOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, Failure(PreSurveyErrorCodes.PreSurveyNotOwned, "You do not have access to this pre-survey.")),
            SelectSimulationResultOutcome.NotEditable => Conflict(Failure(PreSurveyErrorCodes.PreSurveyNotEditable, "A simulation can only be selected while the pre-survey is a draft.")),
            SelectSimulationResultOutcome.SourceChanged => Conflict(Failure(SimulationErrorCodes.SourceChanged, "The surface changed since this simulation was calculated. Reload and simulate again.")),
            SelectSimulationResultOutcome.SimulationNotFound => NotFound(Failure(SimulationErrorCodes.SimulationNotFound, "Simulation was not found.")),
            SelectSimulationResultOutcome.Expired => ExpiredFailure(),
            SelectSimulationResultOutcome.CacheUnavailable => CacheUnavailableFailure(),
            _ => Conflict(Failure(SimulationErrorCodes.SelectionConflict, "Another selection was saved at the same time. Reload and try again.")),
        };
    }

    /// <summary>Removes the selection while the pre-survey is a draft (idempotent).</summary>
    [HttpDelete("selection")]
    [Authorize(Roles = RoleCodes.Customer)]
    [EnableRateLimiting(RateLimitingExtensions.SimulationWritePolicy)]
    public async Task<IActionResult> ClearSelection(Guid preSurveyId, CancellationToken cancellationToken)
    {
        if (GetAuthenticatedUserId() is not { } userId)
        {
            return UnauthorizedFailure();
        }

        return await _selectHandler.ClearAsync(userId, preSurveyId, cancellationToken) switch
        {
            ClearSimulationSelectionOutcome.Cleared => NoContent(),
            ClearSimulationSelectionOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
            ClearSimulationSelectionOutcome.NotOwned => StatusCode(StatusCodes.Status403Forbidden, Failure(PreSurveyErrorCodes.PreSurveyNotOwned, "You do not have access to this pre-survey.")),
            _ => Conflict(Failure(PreSurveyErrorCodes.PreSurveyNotEditable, "The selection is frozen once the pre-survey is submitted.")),
        };
    }

    private IActionResult QueryFailure(SimulationQueryOutcome outcome) => outcome switch
    {
        SimulationQueryOutcome.PreSurveyNotFound => NotFound(Failure(PreSurveyErrorCodes.PreSurveyNotFound, "Pre-survey was not found.")),
        SimulationQueryOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, Failure(SimulationErrorCodes.AccessDenied, "You do not have access to these simulations.")),
        SimulationQueryOutcome.Expired => ExpiredFailure(),
        SimulationQueryOutcome.CacheUnavailable => CacheUnavailableFailure(),
        _ => NotFound(Failure(SimulationErrorCodes.SimulationNotFound, "Simulation was not found.")),
    };

    private IActionResult ExpiredFailure()
        => StatusCode(StatusCodes.Status410Gone, Failure(SimulationErrorCodes.SimulationExpired, "This simulation result has expired. Run the simulation again."));

    private IActionResult CacheUnavailableFailure()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, Failure(SimulationErrorCodes.CacheUnavailable, "Simulation results are temporarily unavailable. Try again shortly."));

    private SimulationViewer? Viewer()
        => GetAuthenticatedUserId() is { } userId
            ? new SimulationViewer(userId, User.IsInRole(RoleCodes.Customer), User.IsInRole(RoleCodes.Sales), User.IsInRole(RoleCodes.Technician))
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
