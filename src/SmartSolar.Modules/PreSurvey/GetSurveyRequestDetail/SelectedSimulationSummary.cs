namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

/// <summary>
/// Compact summary of the simulation the customer selected; the full layout is read from
/// GET /api/pre-surveys/{preSurveyId}/simulations/{simulationId}. Null for requests without one.
/// </summary>
public sealed record SelectedSimulationSummary(
    Guid SimulationId,
    string Status,
    string EnergyStatus,
    bool IsStale,
    string MountingType,
    string ProductSku,
    string ProductName,
    int PanelCount,
    decimal InstalledCapacityKwp,
    decimal? AnnualEnergyKwh,
    DateTimeOffset CreatedAt);
