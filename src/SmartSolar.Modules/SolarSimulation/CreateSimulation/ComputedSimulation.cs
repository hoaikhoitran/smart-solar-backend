using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// Result of the shared (single-flight) calculation. <see cref="SimulationId"/> and <see cref="CreatedAt"/>
/// are assigned once inside the flight, so every caller waiting on it reports the same cached result.
/// </summary>
public sealed record ComputedSimulation(
    string Status,
    string EnergyStatus,
    string ClimateStatus,
    bool IsReusable,
    string? LayoutOrientation,
    int PanelCount,
    decimal InstalledCapacityKwp,
    ComputedAreasView Areas,
    decimal? AnnualEnergyKwh,
    decimal? SpecificYieldKwhPerKwpYear,
    IReadOnlyList<ObstacleDocument> Obstacles,
    InstallationDocument Installation,
    LayoutDocument Layout,
    EnergyDocument Energy,
    ClimateDocument Climate,
    IReadOnlyList<SimulationWarning> Warnings)
{
    public Guid SimulationId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
