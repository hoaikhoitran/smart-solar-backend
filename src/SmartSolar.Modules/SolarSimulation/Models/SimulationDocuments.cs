using System.Text.Json.Serialization;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Modules.SolarSimulation.Models;

/// <summary>World point in meters: E = East, N = North, U = Up, relative to the surface origin.</summary>
public sealed record WorldPoint(double E, double N, double U);

/// <summary>Rotation of the panel frame in the world (E, N, U) frame, Three.js component order.</summary>
public sealed record QuaternionDocument(double X, double Y, double Z, double W);

/// <summary>Footprint on the surface plane, local surface meters. Rotation is from local +X toward +Y.</summary>
public sealed record FootprintDocument(double CenterXM, double CenterYM, double WidthM, double DepthM, double RotationDegree);

/// <summary>Physical module size. Width runs along the panel's horizontal axis, length up its slope.</summary>
public sealed record PhysicalPanelDocument(double WidthM, double LengthM, double ThicknessM);

/// <summary>Front-face center of the module in local surface coordinates plus its height along the surface normal.</summary>
public sealed record FrontCenterDocument(
    [property: JsonPropertyName("xM")] double XM,
    [property: JsonPropertyName("yM")] double YM,
    double HeightAboveSurfaceM);

public sealed record PanelPlacementDocument(
    int Index,
    FootprintDocument Footprint,
    PhysicalPanelDocument Physical,
    FrontCenterDocument FrontCenter,
    WorldPoint WorldCenter,
    QuaternionDocument WorldRotation,
    double LowEdgeClearanceM,
    double HighEdgeHeightM);

public sealed record FrameDocument(
    string LocalFrame,
    string WorldFrame,
    string Azimuth,
    string PanelFrame,
    string Note,
    IReadOnlyList<WorldPoint> SurfaceCornersWorld,
    WorldPoint SurfaceXAxisWorld,
    WorldPoint SurfaceYAxisWorld,
    WorldPoint SurfaceNormalWorld,
    QuaternionDocument PanelRotation);

public sealed record LayoutDocument(
    string Method,
    string? Orientation,
    double RowRotationDegree,
    double? FootprintWidthM,
    double? FootprintDepthM,
    bool FootprintIsConservativeBoundingBox,
    string? NoPanelsReason,
    IReadOnlyList<PanelPlacementDocument> Placements,
    FrameDocument Frame);

/// <summary>Obstacle copy. Height is along the surface normal (rendering only).</summary>
public sealed record ObstacleDocument(
    string Name,
    [property: JsonPropertyName("xM")] decimal XM,
    [property: JsonPropertyName("yM")] decimal YM,
    decimal WidthM,
    decimal LengthM,
    decimal? HeightM,
    IReadOnlyList<WorldPoint> BaseCornersWorld);

public sealed record InstallationValueDocument(
    decimal ValueMm,
    string Source,
    decimal? DocumentedMinimumMm,
    string? DocumentRef,
    string? DocumentSection,
    string? DocumentUrl);

public sealed record InstallationDocument(
    string Units,
    InstallationValueDocument PanelGapMm,
    InstallationValueDocument RowGapMm,
    InstallationValueDocument EdgeSetbackMm,
    InstallationValueDocument ObstacleClearanceMm,
    InstallationValueDocument RackLowEdgeClearanceMm,
    InstallationValueDocument ModuleThicknessMm,
    decimal? ShadeEstimateRowGapMm,
    string ShadingWindow,
    string ProductInstallationSpecStatus,
    IReadOnlyList<string> ProductInstallationSpecErrors,
    string Note);

public static class EnergyScenarioRoles
{
    /// <summary>Flush mounting: PVGIS "building" scenario used as the conservative reference estimate.</summary>
    public const string PrimaryConservativeReference = "PRIMARY_CONSERVATIVE_REFERENCE";

    /// <summary>Flush mounting: PVGIS "free" scenario returned for comparison.</summary>
    public const string Comparison = "COMPARISON";

    /// <summary>Rack mounting: PVGIS "free" scenario.</summary>
    public const string Primary = "PRIMARY";
}

public sealed record EnergyScenarioDocument(
    string Role,
    string MountingPlace,
    string Status,
    ProviderFailure? Failure,
    decimal? AnnualEnergyKwh,
    decimal? AnnualStdDevKwh,
    decimal? SpecificYieldKwhPerKwpYear,
    IReadOnlyList<PvMonthlyEnergy> Monthly,
    PvProviderMetadata? Provider);

public sealed record EnergyDocument(
    string Status,
    string? Reason,
    string EstimateType,
    decimal? AnnualEnergyKwh,
    decimal? SpecificYieldKwhPerKwpYear,
    IReadOnlyList<PvMonthlyEnergy> Monthly,
    string? PrimaryMountingPlace,
    IReadOnlyList<EnergyScenarioDocument> Scenarios,
    IReadOnlyList<string> Assumptions,
    string Disclaimer);

public sealed record ClimateDocument(
    string Status,
    string? Reason,
    ProviderFailure? Failure,
    ClimateContext? Context,
    string Disclaimer);

public sealed record ProductSnapshotView(Guid Id, string Sku, string Name, string Brand, string? Model, decimal RatedPowerW, decimal WidthMm, decimal HeightMm);

public sealed record SurfaceSnapshotView(
    decimal LengthM,
    decimal WidthM,
    decimal TiltDegree,
    decimal AzimuthDegree,
    decimal? Latitude,
    decimal? Longitude,
    IReadOnlyList<ObstacleDocument> Obstacles);

public sealed record MountingView(string Type, decimal PanelTiltDegree, decimal PanelAzimuthDegree, decimal PvgisAspectDegree);

public sealed record ComputedAreasView(
    decimal GrossSurfaceAreaM2,
    decimal ObstacleOccupiedAreaM2,
    decimal AvailableSurfaceAreaM2,
    decimal InstallableAreaM2,
    decimal PanelCoveredAreaM2,
    decimal TotalModuleAreaM2,
    string Note);

public sealed record LayoutView(
    int PanelCount,
    decimal InstalledCapacityKwp,
    ComputedAreasView ComputedAreas,
    LayoutDocument Details);

/// <summary>Full simulation payload for 2D/3D rendering, energy charts and limitations.</summary>
public sealed record SimulationDetail(
    Guid SimulationId,
    Guid PreSurveyId,
    string Status,
    bool IsStale,
    bool IsSelected,
    int PreSurveyGeometryVersion,
    string AlgorithmVersion,
    DateTimeOffset CreatedAt,
    bool EngineeringReviewRequired,
    ProductSnapshotView Product,
    SurfaceSnapshotView Surface,
    MountingView Mounting,
    InstallationDocument Installation,
    LayoutView Layout,
    EnergyDocument Energy,
    ClimateDocument Climate,
    IReadOnlyList<SimulationWarning> Warnings,
    IReadOnlyList<string> Limitations);

public sealed record SimulationListItem(
    Guid SimulationId,
    string Status,
    string EnergyStatus,
    string ClimateStatus,
    bool IsStale,
    bool IsSelected,
    int PreSurveyGeometryVersion,
    string MountingType,
    Guid ProductId,
    string ProductSku,
    string ProductName,
    int PanelCount,
    decimal InstalledCapacityKwp,
    decimal? AnnualEnergyKwh,
    DateTimeOffset CreatedAt);

public static class SimulationLimitations
{
    /// <summary>
    /// V1 limitations. The shading line quotes the design window recorded with the snapshot, so
    /// it stays correct when the window is reconfigured.
    /// </summary>
    public static IReadOnlyList<string> For(string shadingWindow) =>
    [
        "Preliminary estimate. Engineering review is required before any installation decision.",
        "Energy values are PVGIS typical-year estimates from historical data, not a forecast or guarantee.",
        "Climate values are NASA POWER historical averages for the stated period, shown as context only.",
        "The layout is a best-found preliminary result, not a proven optimum.",
        $"Rack row shading is an advisory estimate for the recorded design window ({shadingWindow}), not proof of year-round shade-free operation.",
        "Shading from obstacles and from row ends is not modeled.",
        "Structural, mounting-system, building code and fire code requirements are not checked.",
        "Installation spacing values are in millimetres; each value states its source.",
    ];
}
