using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Calculation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Modules.SolarSimulation.Shading;

/// <summary>What the simulation must do about shading, decided before any provider call.</summary>
public enum ShadingPlanKind
{
    /// <summary>Not computed: <see cref="ShadingPlan.Reason"/> says why (no panels, no location, missing obstacle height, disabled).</summary>
    Unavailable,
    /// <summary>Nothing can cast a shadow on the panels: zero loss, no hourly data needed.</summary>
    NoOccluders,
    /// <summary>Ray casting with the PVGIS hourly series is needed.</summary>
    RayCast,
}

public sealed record ShadingPlan(ShadingPlanKind Kind, string? Reason, PanelShadingCalculator? Calculator);

/// <summary>
/// Builds the 3D shading scene from the simulation's own layout (the actual placements and their orientation), checks the
/// inputs are sufficient, runs the estimate and turns it into the response document. Obstacle heights are never invented:
/// an obstacle without a height makes the estimate UNAVAILABLE instead of reporting a misleading zero loss.
/// </summary>
public static class ShadingAnalysis
{
    public const string SunPositionAlgorithm = "NOAA solar position equations (Meeus), checked against the NREL SPA reference case";

    public static readonly IReadOnlyList<string> Limitations =
    [
        "Linear irradiance-based shading estimate: only the direct beam Gb(i) is reduced, in proportion to the shaded panel area.",
        "Electrical mismatch, cell layout, bypass diodes, strings, inverter clipping and temperature effects of partial shading are not modeled; a panel that is 20 % shaded does not necessarily lose exactly 20 % of its power.",
        "Diffuse-sky and reflected irradiance are not reduced by nearby objects (no sky-view model).",
        "PVGIS hourly values are used unchanged within each hour; the shadow is evaluated at four 15-minute sub-samples.",
        "Only the drawn surface, its obstacles and (rack) the panels themselves cast shadows; terrain horizon is handled by PVGIS.",
    ];

    public static ShadingPlan Plan(SimulationLayoutResult layout, SimulationGeometryInput input, decimal ratedPowerW, bool hasLocation,
        ShadingAnalysisOptions options)
    {
        if (!options.Enabled) return new(ShadingPlanKind.Unavailable, ShadingReasons.Disabled, null);
        if (layout.Placements.Count == 0) return new(ShadingPlanKind.Unavailable, ShadingReasons.NoPanels, null);
        if (!hasLocation) return new(ShadingPlanKind.Unavailable, ShadingReasons.LocationMissing, null);
        if (input.Obstacles.Any(o => o.HeightM is null)) return new(ShadingPlanKind.Unavailable, ShadingReasons.ObstacleHeightMissing, null);

        var isRack = input.MountingType == MountingTypes.Rack;
        var obstacles = input.Obstacles.Where(o => o.HeightM > 0).Select(ToBox).ToList();
        if (obstacles.Count == 0 && (!isRack || layout.Placements.Count == 1)) return new(ShadingPlanKind.NoOccluders, ShadingReasons.NoOccluders, null);

        var surface = new SurfaceFrame((double)input.SurfaceTiltDegree, (double)input.SurfaceAzimuthDegree);
        var orientation = new PanelOrientation(layout.PanelTiltDegree, layout.PanelAzimuthDegree);
        var panels = layout.Placements.Select(p => new PanelFace(p.Index, p.WorldCenter, orientation.WidthAxis, orientation.UpAxis, orientation.Normal,
            p.PhysicalWidthM, p.PhysicalLengthM, (double)ratedPowerW)).ToList();
        // Flush panels lie in one plane parallel to the surface and cannot shade each other; rack rows can.
        var scene = new ShadingScene(surface, (double)input.SurfaceWidthM, (double)input.SurfaceLengthM, obstacles, isRack ? panels : []);
        var calculator = new PanelShadingCalculator(scene, panels, Math.Max(1, options.SamplesAlongWidth), Math.Max(1, options.SamplesAlongLength),
            Math.Clamp(options.DirectionBinDegree, 0.1, 10));
        return new(ShadingPlanKind.RayCast, null, calculator);
    }

    /// <summary>
    /// Ray–occluder tests needed for these hours: distinct sun-direction bins (with direct irradiance) × samples × occluders.
    /// Computed before ray casting so an oversized scene answers COMPUTATION_LIMIT instead of slowing the request down.
    /// </summary>
    public static long EstimateWork(PanelShadingCalculator calculator, int occluderCount, double latitude, double longitude, IReadOnlyList<PvHourlyRecord> hours)
    {
        var bins = new HashSet<(int, int)>();
        foreach (var hour in hours)
        {
            if (hour.BeamW <= 0) continue;
            foreach (var minutes in ShadingEnergyCalculator.SubSampleMinutes)
            {
                var sun = SunPositionCalculator.Calculate(latitude, longitude, hour.HourStartUtc.AddMinutes(minutes));
                if (sun.IsAboveHorizon) bins.Add(calculator.BinOf(sun));
            }
        }
        return (long)bins.Count * calculator.Panels.Count * calculator.SamplesPerPanel * occluderCount;
    }

    private static ObstacleBox ToBox(SurfaceObstacle o)
        => new((double)o.XM, (double)o.YM, (double)(o.XM + o.WidthM), (double)(o.YM + o.LengthM), (double)o.HeightM!.Value);

    /// <summary>
    /// The typical-year result. The shading loss fraction of each calendar month comes from the PVGIS hourly series of the
    /// same system; it is applied to the PVGIS typical-year baseline of that month, so the existing PVGIS estimate stays the
    /// reference baseline and no PVGIS configurations are mixed.
    /// </summary>
    public static ShadingDocument Succeeded(ShadingAnalysisOptions options, IReadOnlyList<PvMonthlyEnergy> baselineMonthly, ShadingEnergyResult? estimate,
        IReadOnlyList<int> placementIndices, int samplesPerPanel, string? reason, int? startYear, int? endYear)
    {
        var monthly = baselineMonthly.OrderBy(m => m.Month).Select(m =>
        {
            var fraction = estimate?.Monthly.FirstOrDefault(x => x.Month == m.Month)?.LossFraction ?? 0;
            var loss = Math.Round(m.EnergyKwh * (decimal)fraction, 2);
            var after = m.EnergyKwh - loss;
            return new ShadingMonthDocument(m.Month, m.EnergyKwh, after, loss, m.EnergyKwh > 0 ? Math.Round(100 * loss / m.EnergyKwh, 2) : 0);
        }).ToList();
        var before = monthly.Sum(m => m.EnergyBeforeShadingKwh);
        var lossKwh = monthly.Sum(m => m.EnergyLossKwh);

        var threshold = options.ShadedPanelThresholdPercent / 100;
        var panels = placementIndices.Select(i => new ShadingPanelDocument(i,
            Math.Round(estimate?.AnnualDirectShadedFractionByPanel.GetValueOrDefault(i) ?? 0, 4))).ToList();

        return new ShadingDocument(
            ComponentStatusCodes.Succeeded, reason, null, ShadingMethods.RayCast3dPvgisHourlyLinear, SunPositionAlgorithm,
            (int)(ShadingEnergyCalculator.SubSampleHours * 60), startYear, endYear, estimate?.YearCount,
            before > 0 ? Math.Round(100 * lossKwh / before, 2) : 0, lossKwh, before, before - lossKwh,
            panels.Count(p => p.AnnualDirectShadedFraction >= threshold && p.AnnualDirectShadedFraction > 0), options.ShadedPanelThresholdPercent,
            panels, monthly,
            estimate is null ? null : Math.Round((decimal)estimate.AnnualEnergyBeforeKwh, 2),
            estimate is null ? null : Math.Round(estimate.MeanSunHeightDeviationDegree, 3),
            samplesPerPanel, options.DirectionBinDegree, Limitations);
    }

    /// <summary>A shading document that is not SUCCEEDED: no loss values at all (never a fabricated zero).</summary>
    public static ShadingDocument NotSucceeded(string status, string reason, ProviderFailure? failure, ShadingAnalysisOptions options, int? startYear, int? endYear)
        => new(status, reason, failure, ShadingMethods.RayCast3dPvgisHourlyLinear, SunPositionAlgorithm, (int)(ShadingEnergyCalculator.SubSampleHours * 60),
            startYear, endYear, null, null, null, null, null, null, options.ShadedPanelThresholdPercent, [], [], null, null,
            options.SamplesAlongWidth * options.SamplesAlongLength, options.DirectionBinDegree, Limitations);
}
