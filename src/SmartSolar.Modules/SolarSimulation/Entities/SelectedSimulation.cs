namespace SmartSolar.Modules.SolarSimulation.Entities;

/// <summary>
/// Compact, durable summary of the one simulation a customer selected for a pre-survey
/// (table <c>selected_simulation</c>). Full simulation results are temporary (distributed cache)
/// and only optionally copied to object storage (<see cref="SnapshotObjectKey"/>); this row is what
/// survives cache expiry and what Sales and technicians read downstream.
/// <para>
/// One row per pre-survey. Re-selecting while the pre-survey is Draft replaces the row; after
/// submission the selection is frozen. <see cref="CreatedAt"/> is when the simulation was calculated
/// (copied from the cached result), <see cref="SelectedAt"/> when it was selected.
/// </para>
/// </summary>
public sealed class SelectedSimulation
{
    public Guid Id { get; set; }
    public Guid PreSurveyId { get; set; }

    /// <summary>Id of the (temporary) simulation result; referenced by pre_survey.selected_simulation_id.</summary>
    public Guid SimulationId { get; set; }

    public Guid SelectedByUserId { get; set; }
    public DateTimeOffset SelectedAt { get; set; }

    public int PreSurveyGeometryVersion { get; set; }
    public string AlgorithmVersion { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string EnergyStatus { get; set; } = null!;
    public string MountingType { get; set; } = null!;

    public Guid ProductId { get; set; }
    public string ProductSku { get; set; } = null!;
    public string ProductName { get; set; } = null!;

    public int PanelCount { get; set; }
    public decimal InstalledCapacityKwp { get; set; }
    public decimal? AnnualEnergyKwh { get; set; }
    public decimal? SpecificYieldKwhPerKwpYear { get; set; }
    public decimal PanelTiltDegree { get; set; }
    public decimal PanelAzimuthDegree { get; set; }

    /// <summary>Optional durable full snapshot (S3). All four are set together or all null.</summary>
    public string? SnapshotBucket { get; set; }
    public string? SnapshotObjectKey { get; set; }
    public string? SnapshotSha256 { get; set; }
    public long? SnapshotSizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
