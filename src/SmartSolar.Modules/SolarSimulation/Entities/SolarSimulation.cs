namespace SmartSolar.Modules.SolarSimulation.Entities;

/// <summary>
/// Immutable snapshot of one simulation. Rows are insert-only: product values, geometry,
/// installation values and provider results are copied in so later Catalog or
/// pre-survey changes never alter an existing snapshot.
/// </summary>
public sealed class SolarSimulation
{
    public Guid Id { get; set; }
    public Guid PreSurveyId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public int PreSurveyGeometryVersion { get; set; }
    public string InputFingerprint { get; set; } = null!;

    /// <summary>True when no provider failed, so an identical request may reuse this snapshot.</summary>
    public bool IsReusable { get; set; }

    public string Status { get; set; } = null!;
    public string EnergyStatus { get; set; } = null!;
    public string ClimateStatus { get; set; } = null!;
    public string AlgorithmVersion { get; set; } = null!;

    public Guid ProductId { get; set; }
    public string ProductSku { get; set; } = null!;
    public string ProductName { get; set; } = null!;
    public string ProductBrand { get; set; } = null!;
    public string? ProductModel { get; set; }
    public decimal ProductRatedPowerW { get; set; }
    public decimal ProductWidthMm { get; set; }
    public decimal ProductHeightMm { get; set; }
    public string? ProductInstallationSpec { get; set; }

    public decimal SurfaceLengthM { get; set; }
    public decimal SurfaceWidthM { get; set; }
    public decimal SurfaceTiltDegree { get; set; }
    public decimal SurfaceAzimuthDegree { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public string MountingType { get; set; } = null!;
    public decimal PanelTiltDegree { get; set; }
    public decimal PanelAzimuthDegree { get; set; }

    public string? LayoutOrientation { get; set; }
    public int PanelCount { get; set; }
    public decimal InstalledCapacityKwp { get; set; }

    public decimal GrossSurfaceAreaM2 { get; set; }
    public decimal ObstacleOccupiedAreaM2 { get; set; }
    public decimal AvailableSurfaceAreaM2 { get; set; }
    public decimal InstallableAreaM2 { get; set; }
    public decimal PanelCoveredAreaM2 { get; set; }
    public decimal TotalModuleAreaM2 { get; set; }

    public decimal? AnnualEnergyKwh { get; set; }
    public decimal? SpecificYieldKwhPerKwpYear { get; set; }

    /// <summary>
    /// JSON documents (jsonb), written once. Energy and Climate are always present: when a
    /// provider fails, is skipped or does not apply, the document records that status.
    /// </summary>
    public string Obstacles { get; set; } = null!;
    public string Installation { get; set; } = null!;
    public string Layout { get; set; } = null!;
    public string Warnings { get; set; } = null!;
    public string Energy { get; set; } = null!;
    public string Climate { get; set; } = null!;
}
