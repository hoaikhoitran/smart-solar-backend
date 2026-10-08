using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// Request to calculate and store a simulation. <see cref="ExpectedGeometryVersion"/> is the
/// version the client is looking at; a mismatch is rejected so the snapshot matches the
/// customer's view. Panel angles are absolute (compass azimuth) and only used for RACK;
/// FLUSH panels always follow the surface. Installation values are millimetres.
/// </summary>
public sealed record CreateSimulationCommand(
    Guid UserId,
    Guid PreSurveyId,
    int? ExpectedGeometryVersion,
    Guid ProductId,
    string? MountingType,
    decimal? PanelTiltDegree,
    decimal? PanelAzimuthDegree,
    InstallationRequest Installation,
    decimal? SystemLossPercent);

public enum CreateSimulationOutcome
{
    Created,
    Reused,
    PreSurveyNotFound,
    NotOwned,
    NotEditable,
    SourceChanged,
    SurfaceNotDefined,
    ProductNotFound,
    Rejected
}

public sealed record SimulationProblem(string Code, string? Parameter, string Message);

public sealed record CreateSimulationResult(
    CreateSimulationOutcome Outcome,
    SimulationDetail? Simulation,
    string? ErrorCode,
    IReadOnlyList<SimulationProblem> Problems)
{
    public static CreateSimulationResult Success(CreateSimulationOutcome outcome, SimulationDetail detail)
        => new(outcome, detail, null, []);

    public static CreateSimulationResult Fail(CreateSimulationOutcome outcome, string? code = null, IReadOnlyList<SimulationProblem>? problems = null)
        => new(outcome, null, code, problems ?? []);
}
