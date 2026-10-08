namespace SmartSolar.Modules.SolarSimulation.Options;

/// <summary>
/// Configuration section "SolarSimulation". Every value here is a configurable V1
/// calculation assumption or computational limit. None is a manufacturer requirement,
/// building code or fire code value. All installation values are in millimetres (mm):
/// 20 mm = 2 cm.
/// </summary>
public sealed class SolarSimulationOptions
{
    public const string SectionName = "SolarSimulation";

    public InstallationDefaultsOptions PreliminaryDefaults { get; set; } = new();

    public SimulationLimitsOptions Limits { get; set; } = new();

    public ShadingWindowOptions Shading { get; set; } = new();

    /// <summary>Evenly spaced row-offset samples tried per orientation.</summary>
    public int LayoutOffsetSamples { get; set; } = 20;

    /// <summary>PVGIS manual's documented default for overall system losses (percent).</summary>
    public decimal DefaultSystemLossPercent { get; set; } = 14m;

    /// <summary>Preliminary review threshold for the high edge of rack-mounted panels (mm above surface).</summary>
    public decimal RackSupportHeightWarningMm { get; set; } = 1500m;

    /// <summary>Relative difference between declared total area and computed gross area that triggers a warning.</summary>
    public decimal DeclaredAreaMismatchRatio { get; set; } = 0.10m;

    /// <summary>Overall time budget for all provider calls of one simulation (seconds).</summary>
    public int ProviderBudgetSeconds { get; set; } = 25;
}

/// <summary>
/// Preliminary defaults (mm). Used only when the request omits a value, and never below a
/// documented manufacturer minimum. Set a value to null to require it in every request.
/// </summary>
public sealed class InstallationDefaultsOptions
{
    public decimal? PanelGapMm { get; set; } = 20m;
    public decimal? FlushRowGapMm { get; set; } = 20m;
    public decimal? RackRowGapFallbackMm { get; set; } = 1000m;
    public decimal? EdgeSetbackMm { get; set; } = 300m;
    public decimal? ObstacleClearanceMm { get; set; } = 300m;
    public decimal? RackLowEdgeClearanceMm { get; set; } = 200m;
    public decimal? ModuleThicknessMm { get; set; } = 40m;
}

/// <summary>Computational limits for V1, not certified engineering restrictions.</summary>
public sealed class SimulationLimitsOptions
{
    public decimal MaxSurfaceSideM { get; set; } = 200m;
    public int MaxPlacements { get; set; } = 5000;
    public int MaxObstacles { get; set; } = 50;
    public int MaxObstacleNameLength { get; set; } = 100;
    public decimal MaxObstacleHeightM { get; set; } = 100m;
    public decimal MaxSpacingMm { get; set; } = 5000m;
    public decimal MaxRackClearanceMm { get; set; } = 3000m;
    public decimal MinSystemLossPercent { get; set; } = 0m;
    public decimal MaxSystemLossPercent { get; set; } = 50m;

    /// <summary>Upper bound on layout work (rows + obstacle clips + placements) per calculation.</summary>
    public long MaxLayoutWorkUnits { get; set; } = 10_000_000;
}

/// <summary>Advisory row-shading design window: both solstices, 09:00–15:00 solar time.</summary>
public sealed class ShadingWindowOptions
{
    public double DeclinationDegree { get; set; } = 23.44;
    public double StartSolarHour { get; set; } = 9;
    public double EndSolarHour { get; set; } = 15;
    public double StepMinutes { get; set; } = 15;
}
