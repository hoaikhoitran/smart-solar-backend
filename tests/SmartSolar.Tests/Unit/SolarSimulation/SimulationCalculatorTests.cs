using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Calculation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Options;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class SimulationCalculatorTests
{
    private static ResolvedInstallation Installation(string mounting, decimal? fixedRowGapMm, bool useShade = false, decimal thicknessMm = 40m, decimal clearanceMm = 200m)
    {
        ResolvedParameter P(decimal v) => new(v, InstallationSources.PreliminaryDefault, null, null, null, null);
        return new ResolvedInstallation(
            PanelGap: P(20m),
            EdgeSetback: P(300m),
            ObstacleClearance: P(300m),
            RackLowEdgeClearance: mounting == MountingTypes.Rack ? P(clearanceMm) : new ResolvedParameter(0m, InstallationSources.NotApplicable, null, null, null, null),
            ModuleThickness: P(thicknessMm),
            RowGap: new RowGapPolicy(fixedRowGapMm, fixedRowGapMm is null ? null : InstallationSources.SiteSpecified, null, null, null, null, useShade));
    }

    private static SimulationGeometryInput Input(
        string mounting = MountingTypes.Flush,
        decimal surfaceTilt = 15m, decimal surfaceAzimuth = 180m,
        decimal panelTilt = 15m, decimal panelAzimuth = 180m,
        decimal? latitude = 10.7769m,
        IReadOnlyList<SurfaceObstacle>? obstacles = null,
        ResolvedInstallation? installation = null,
        decimal length = 12m, decimal width = 8m)
        => new(
            length, width, surfaceTilt, surfaceAzimuth, obstacles ?? [],
            PanelWidthMm: 1134m, PanelHeightMm: 2278m,
            mounting, panelTilt, panelAzimuth,
            installation ?? Installation(mounting, 20m),
            latitude,
            ShadingDesignWindow.Default,
            OffsetSamples: 20,
            MaxPlacements: 5000,
            RackSupportHeightWarningMm: 1500m);

    [Fact]
    public void Flush_layout_places_panels_with_consistent_capacity_inputs()
    {
        var result = SimulationCalculator.Calculate(Input());

        Assert.True(result.Placements.Count > 0);
        Assert.Equal(result.Placements.Count, result.Placements.Select(p => p.Index).Distinct().Count());
        Assert.NotNull(result.Orientation);
        Assert.False(result.FootprintIsConservative);
        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.ModuleThicknessAssumed);
    }

    [Fact]
    public void Obstacle_heights_and_shading_limitation_are_reported()
    {
        var result = SimulationCalculator.Calculate(Input(obstacles: [new SurfaceObstacle("Water Tank", 6m, 4m, 1.5m, 2m, 1.8m)]));

        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.ObstacleShadingNotModeled);
    }

    [Fact]
    public void Rack_without_location_reports_unevaluated_shading()
    {
        var result = SimulationCalculator.Calculate(Input(
            MountingTypes.Rack, surfaceTilt: 0, panelTilt: 12, latitude: null,
            installation: Installation(MountingTypes.Rack, 1000m)));

        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.ShadeSpacingNotEvaluated);
    }

    [Fact]
    public void Rack_row_gap_comes_from_the_shade_estimate_when_requested()
    {
        var result = SimulationCalculator.Calculate(Input(
            MountingTypes.Rack, surfaceTilt: 0, panelTilt: 12,
            installation: Installation(MountingTypes.Rack, null, useShade: true)));

        Assert.Equal(InstallationSources.ComputedShadeEstimate, result.RowGap.Source);
        Assert.True(result.RowGap.ValueMm > 0);
        Assert.NotNull(result.ShadeEstimateRowGapMm);
    }

    [Fact]
    public void Site_row_gap_below_the_shade_estimate_is_kept_with_a_warning()
    {
        var result = SimulationCalculator.Calculate(Input(
            MountingTypes.Rack, surfaceTilt: 0, panelTilt: 30,
            installation: Installation(MountingTypes.Rack, 50m)));

        Assert.Equal(50m, result.RowGap.ValueMm);
        Assert.Equal(InstallationSources.SiteSpecified, result.RowGap.Source);
        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.RowGapBelowShadeEstimate);
    }

    [Fact]
    public void Rack_on_inclined_surface_with_different_azimuth_is_flagged_conservative()
    {
        var result = SimulationCalculator.Calculate(Input(
            MountingTypes.Rack, surfaceTilt: 20, panelTilt: 30, panelAzimuth: 120,
            installation: Installation(MountingTypes.Rack, 500m)));

        Assert.True(result.FootprintIsConservative);
        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.RackOnInclinedSurface);
        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.FootprintConservativeBoundingBox);
    }

    [Fact]
    public void High_rack_supports_are_flagged()
    {
        // A flat panel on a steep roof needs tall supports on its low side.
        var result = SimulationCalculator.Calculate(Input(
            MountingTypes.Rack, surfaceTilt: 45, panelTilt: 5, panelAzimuth: 180,
            installation: Installation(MountingTypes.Rack, 500m)));

        Assert.Contains(result.Warnings, w => w.Code == SimulationWarningCodes.RackSupportHeightAbovePreliminaryThreshold);
    }

    [Fact]
    public void Zero_panels_return_a_structured_reason()
    {
        // 0.5 m surface with a 300 mm setback on each side leaves no region at all.
        var result = SimulationCalculator.Calculate(Input(length: 0.5m, width: 0.5m));

        Assert.Empty(result.Placements);
        Assert.Equal(NoPanelsReasons.SetbackConsumesSurface, result.NoPanelsReason);
    }

    [Fact]
    public void Placement_world_center_uses_the_surface_frame()
    {
        // Horizontal south-facing surface, flush panels: world center = (x, -y, thickness).
        var result = SimulationCalculator.Calculate(Input(surfaceTilt: 0, panelTilt: 0));
        var first = result.Placements[0];

        Near(first.FrontCenterXM, first.WorldCenter.X, 1e-3);
        Near(-first.FrontCenterYM, first.WorldCenter.Y, 1e-3);
        Near(0.04, first.WorldCenter.Z, 1e-3);
    }

    public static TheoryData<string, decimal, decimal, decimal, decimal> Configurations => new()
    {
        { MountingTypes.Flush, 15m, 180m, 15m, 180m },
        { MountingTypes.Flush, 0m, 90m, 0m, 90m },
        { MountingTypes.Rack, 0m, 180m, 25m, 180m },
        { MountingTypes.Rack, 0m, 180m, 20m, 135m },
        { MountingTypes.Rack, 20m, 180m, 30m, 180m },
        { MountingTypes.Rack, 20m, 180m, 30m, 120m },
        { MountingTypes.Rack, 35m, 180m, 10m, 180m },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Physical_panels_never_intersect_each_other_obstacles_or_the_surface(
        string mounting, decimal surfaceTilt, decimal surfaceAzimuth, decimal panelTilt, decimal panelAzimuth)
    {
        var obstacles = new[]
        {
            new SurfaceObstacle("Rock", 2m, 3m, 2m, 1m, 0.5m),
            new SurfaceObstacle("Water Tank", 5m, 7m, 1.5m, 2m, 1.8m),
        };
        var installation = mounting == MountingTypes.Rack
            ? Installation(MountingTypes.Rack, 0m, thicknessMm: 40m, clearanceMm: 100m)
            : Installation(MountingTypes.Flush, 0m);
        var input = Input(mounting, surfaceTilt, surfaceAzimuth, panelTilt, panelAzimuth,
            obstacles: obstacles, installation: installation) with { };
        var installationWithNoGaps = installation with
        {
            PanelGap = installation.PanelGap with { ValueMm = 0m },
            ObstacleClearance = installation.ObstacleClearance with { ValueMm = 0m },
        };
        input = input with { Installation = installationWithNoGaps };

        var result = SimulationCalculator.Calculate(input);
        Assert.NotEmpty(result.Placements);

        var surface = new SurfaceFrame((double)surfaceTilt, (double)surfaceAzimuth);
        var panelBoxes = result.Placements.Select(p => PanelBox(p, result)).ToList();

        // Lowest point of every module sits at the configured clearance, never below the surface.
        foreach (var box in panelBoxes)
        {
            var minHeight = box.Corners.Min(c => c.Dot(surface.Normal));
            Assert.True(minHeight >= (double)installation.RackLowEdgeClearance.ValueMm / 1000 - 1e-6, $"Module dips below clearance: {minHeight}");
        }

        for (var a = 0; a < panelBoxes.Count; a++)
        {
            for (var b = a + 1; b < panelBoxes.Count; b++)
            {
                if ((panelBoxes[a].Center - panelBoxes[b].Center).Length > 5) continue;
                Assert.False(Intersects(panelBoxes[a], panelBoxes[b]), $"Modules {a} and {b} intersect.");
            }

            foreach (var obstacle in obstacles)
            {
                Assert.False(Intersects(panelBoxes[a], ObstacleBox(obstacle, surface)), $"Module {a} intersects {obstacle.Name}.");
            }
        }
    }

    private sealed record Box(Vec3 Center, Vec3[] Axes, Vec3[] Corners);

    private static Box PanelBox(SimulatedPanel panel, SimulationLayoutResult result)
    {
        var orientation = new PanelOrientation(result.PanelTiltDegree, result.PanelAzimuthDegree);
        var w = orientation.WidthAxis;
        var u = orientation.UpAxis;
        var n = orientation.Normal;
        var corners = new List<Vec3>();
        foreach (var d in new[] { 0.0, panel.ThicknessM })
            foreach (var sw in new[] { -1.0, 1.0 })
                foreach (var su in new[] { -1.0, 1.0 })
                    corners.Add(panel.WorldCenter + sw * panel.PhysicalWidthM / 2 * w + su * panel.PhysicalLengthM / 2 * u - d * n);
        var center = (1.0 / corners.Count) * corners.Aggregate(new Vec3(0, 0, 0), (acc, c) => acc + c);
        return new Box(center, [w, u, n], corners.ToArray());
    }

    private static Box ObstacleBox(SurfaceObstacle o, SurfaceFrame surface)
    {
        var corners = new List<Vec3>();
        foreach (var h in new[] { 0.0, (double)(o.HeightM ?? 0) })
            foreach (var x in new[] { (double)o.XM, (double)(o.XM + o.WidthM) })
                foreach (var y in new[] { (double)o.YM, (double)(o.YM + o.LengthM) })
                    corners.Add(surface.ToWorld(x, y, h));
        var center = (1.0 / corners.Count) * corners.Aggregate(new Vec3(0, 0, 0), (acc, c) => acc + c);
        return new Box(center, [surface.XAxis, surface.YAxis, surface.Normal], corners.ToArray());
    }

    /// <summary>Separating axis test for two convex boxes; touching counts as not intersecting.</summary>
    private static bool Intersects(Box a, Box b)
    {
        var axes = new List<Vec3>(a.Axes.Concat(b.Axes));
        foreach (var x in a.Axes)
            foreach (var y in b.Axes)
            {
                var c = x.Cross(y);
                if (c.Length > 1e-9) axes.Add(c.Normalized());
            }

        foreach (var axis in axes)
        {
            var (aMin, aMax) = (a.Corners.Min(c => c.Dot(axis)), a.Corners.Max(c => c.Dot(axis)));
            var (bMin, bMax) = (b.Corners.Min(c => c.Dot(axis)), b.Corners.Max(c => c.Dot(axis)));
            if (aMax <= bMin + 1e-7 || bMax <= aMin + 1e-7) return false;
        }
        return true;
    }
}
