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
    decimal? Longitude,
    IReadOnlyList<Guid>? AssignedTechnicianUserIds = null);

public enum SelectSimulationOutcome
{
    Selected,
    SourceChanged,
    NotDraft,

    /// <summary>A concurrent selection inserted its row first (unique violation); nothing was changed.</summary>
    Conflict
}

public enum ClearSelectionOutcome
{
    Cleared,
    NotDraft
}

/// <summary>
/// PostgreSQL side of simulations: the pre-survey read model and the compact
/// <c>selected_simulation</c> summary. Full results are never stored here (see ISimulationResultCache).
/// </summary>
public interface ISolarSimulationUnitOfWork
{
    Task<SimulationSource?> GetSourceAsync(Guid preSurveyId, CancellationToken cancellationToken);

    /// <summary>The stored selection summary of this pre-survey, or null.</summary>
    Task<Entities.SelectedSimulation?> GetSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction, only while the pre-survey is Draft at the summary's geometry version:
    /// replace this pre-survey's selected_simulation row with <paramref name="selection"/> and point
    /// pre_survey.selected_simulation_id at it. Nothing changes otherwise.
    /// </summary>
    Task<SelectSimulationOutcome> TrySelectAsync(Entities.SelectedSimulation selection, CancellationToken cancellationToken);

    /// <summary>Removes the selection while the pre-survey is Draft (idempotent when nothing is selected).</summary>
    Task<ClearSelectionOutcome> TryClearSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken);
}
