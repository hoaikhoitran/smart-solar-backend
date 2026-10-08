using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class PreSurvey
{
    public Guid Id { get; set; }

    public Guid PropertyId { get; set; }

    public decimal? TotalAreaM2 { get; set; }

    public decimal? UsableAreaM2 { get; set; }

    public decimal? TiltDegree { get; set; }

    public decimal? AzimuthDegree { get; set; }

    public bool? HasObstruction { get; set; }

    /// <summary>Surface length in meters along the local Y axis (down-slope), on the surface plane.</summary>
    public decimal? SurfaceLengthM { get; set; }

    /// <summary>Surface width in meters along the local X axis, on the surface plane.</summary>
    public decimal? SurfaceWidthM { get; set; }

    /// <summary>Client-drawn obstacles as a JSON array (jsonb); null until a surface is saved.</summary>
    public string? Obstacles { get; set; }

    /// <summary>
    /// Incremented whenever tilt, azimuth, surface dimensions or obstacles change.
    /// Simulations record the version they were calculated from; a mismatch means stale.
    /// </summary>
    public int GeometryVersion { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Every pre-survey write checks and increments it,
    /// so a stale read can never overwrite a newer change or a submission.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Simulation the customer last created or reused; always one of this pre-survey's own.</summary>
    public Guid? SelectedSimulationId { get; set; }

    public PreSurveyStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PropertySite Property { get; set; } = null!;
    public SurveyRequest? SurveyRequest { get; set; }
}