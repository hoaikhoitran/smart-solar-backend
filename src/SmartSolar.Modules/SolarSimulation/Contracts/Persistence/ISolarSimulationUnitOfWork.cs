using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.SolarSimulation.Contracts.Persistence;

/// <summary>Read model of a pre-survey as the simulation module needs it.</summary>
public sealed record SimulationSource(
    Guid PreSurveyId,
    Guid OwnerUserId,
    Guid? AssignedSaleId,
    PreSurveyStatus Status,
    int GeometryVersion,
    Guid? SelectedSimulationId,
    decimal? SurfaceLengthM,
    decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree,
    decimal? SurfaceAzimuthDegree,
    string? ObstaclesJson,
    decimal? DeclaredTotalAreaM2,
    decimal? Latitude,
    decimal? Longitude);

/// <summary>List row; excludes the large JSON documents.</summary>
public sealed record SimulationSummary(
    Guid SimulationId,
    int PreSurveyGeometryVersion,
    string Status,
    string EnergyStatus,
    string ClimateStatus,
    string MountingType,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    int PanelCount,
    decimal InstalledCapacityKwp,
    decimal? AnnualEnergyKwh,
    DateTimeOffset CreatedAt);

public enum SaveSimulationOutcome
{
    Saved,
    DuplicateFingerprint,
    SourceChanged,
    NotDraft
}

public enum SelectSimulationOutcome
{
    Selected,
    SourceChanged,
    NotDraft
}

public interface ISolarSimulationUnitOfWork
{
    Task<SimulationSource?> GetSourceAsync(Guid preSurveyId, CancellationToken cancellationToken);

    Task<Entities.SolarSimulation?> FindReusableAsync(Guid preSurveyId, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: lock the pre-survey row only if it is still Draft at the snapshot's
    /// geometry version, insert the snapshot and select it. Nothing is stored otherwise.
    /// </summary>
    Task<SaveSimulationOutcome> TrySaveAndSelectAsync(Entities.SolarSimulation simulation, CancellationToken cancellationToken);

    /// <summary>
    /// Selects an existing snapshot only when it belongs to this pre-survey, was calculated
    /// at the current geometry version, and the pre-survey is still Draft.
    /// </summary>
    Task<SelectSimulationOutcome> TrySelectAsync(Guid preSurveyId, Guid simulationId, int geometryVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<SimulationSummary>> ListAsync(Guid preSurveyId, CancellationToken cancellationToken);

    Task<Entities.SolarSimulation?> FindAsync(Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken);
}
