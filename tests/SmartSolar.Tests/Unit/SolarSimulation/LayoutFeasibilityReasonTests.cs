using SmartSolar.Modules.SolarSimulation.Geometry;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// Area rules (surface, obstacle union, available and installable area) give precise reasons
/// when no panel fits; the geometric layout stays the authority on whether a panel fits.
/// </summary>
public sealed class LayoutFeasibilityReasonTests
{
    private static PanelFootprint Box(double width, double depth)
        => PanelFootprintCalculator.Calculate(new SurfaceFrame(0, 180), new PanelOrientation(0, 180), width, depth, 0, 0);

    private static LayoutRequest Request(double w, double l, double panelShort, double panelLong,
        double setback = 0, double clearance = 0, IReadOnlyList<LayoutObstacle>? obstacles = null)
        => new(w, l, setback, clearance, 0, obstacles ?? [],
            new Dictionary<LayoutOrientation, OrientationCandidate>
            {
                [LayoutOrientation.Portrait] = new(Box(panelShort, panelLong), 0),
                [LayoutOrientation.Landscape] = new(Box(panelLong, panelShort), 0),
            }, 20, 5000);

    [Fact]
    public void Overlapping_obstacles_covering_the_surface_report_full_coverage_counted_once()
    {
        // Two overlapping 3 x 4 obstacles on a 4 x 4 surface: union = 16 (not 24).
        var result = PanelLayoutCalculator.Calculate(Request(4, 4, 1, 2,
            obstacles: [new LayoutObstacle(0, 0, 3, 4), new LayoutObstacle(1, 0, 3, 4)]));

        Assert.Equal(NoPanelsReasons.ObstaclesCoverSurface, result.NoPanelsReason);
        Near(16, result.Areas.ObstacleOccupiedAreaM2);
        Near(0, result.Areas.AvailableSurfaceAreaM2);
    }

    [Fact]
    public void Available_area_below_one_panel_is_reported()
    {
        // 6 x 1 = 6 m² surface, obstacles leave 1.5 m² free in scattered strips; one panel is 2 m².
        var obstacles = new[] { new LayoutObstacle(0.5, 0, 2, 1), new LayoutObstacle(3, 0, 2.5, 1) };

        var result = PanelLayoutCalculator.Calculate(Request(6, 1, 1, 2, obstacles: obstacles));

        Assert.Empty(result.Placements);
        Near(1.5, result.Areas.AvailableSurfaceAreaM2);
        Assert.Equal(NoPanelsReasons.AvailableAreaBelowPanelArea, result.NoPanelsReason);
    }

    [Fact]
    public void Installable_area_below_one_footprint_is_reported_when_clearance_eats_the_space()
    {
        // 6 x 2 surface, a 1 x 1 obstacle (available 11 m²); a 2.2 m clearance grows it to 5.4 x 2 inside
        // the surface, leaving 12 - 10.8 = 1.2 m² installable, below one 2 m² footprint.
        var result = PanelLayoutCalculator.Calculate(Request(6, 2, 1, 2, clearance: 2.2,
            obstacles: [new LayoutObstacle(2.5, 0.5, 1, 1)]));

        Assert.Empty(result.Placements);
        Near(11, result.Areas.AvailableSurfaceAreaM2);
        Near(1.2, result.Areas.InstallableAreaM2);
        Assert.Equal(NoPanelsReasons.InstallableAreaBelowPanelFootprint, result.NoPanelsReason);
    }

    [Fact]
    public void Enough_area_but_no_complete_panel_position_is_a_geometric_block()
    {
        // 4 x 2 surface, full-height 1 m strip in the middle, clearance 0.6: the grown obstacle spans
        // x = 0.9..3.1, leaving 8 - 4.4 = 3.6 m² installable (more than one 2 m² footprint) split into
        // two 0.9 m wide strips where no complete 1 x 2 panel fits.
        var result = PanelLayoutCalculator.Calculate(Request(4, 2, 1, 2, clearance: 0.6,
            obstacles: [new LayoutObstacle(1.5, 0, 1, 2)]));

        Assert.Empty(result.Placements);
        Near(3.6, result.Areas.InstallableAreaM2);
        Assert.Equal(NoPanelsReasons.ObstaclesBlockAllPositions, result.NoPanelsReason);
    }

    [Fact]
    public void Area_alone_is_not_proof_of_fit()
    {
        // 10 x 0.9 m: 9 m² of area for a 2 m² panel, but its short side (1 m) is wider than the strip.
        var result = PanelLayoutCalculator.Calculate(Request(10, 0.9, 1, 2));

        Assert.Empty(result.Placements);
        Assert.True(result.Areas.InstallableAreaM2 > 2);
        Assert.Equal(NoPanelsReasons.PanelLargerThanInstallableRegion, result.NoPanelsReason);
    }

    [Fact]
    public void Rack_footprint_not_width_times_height_drives_the_installable_check()
    {
        // A 2 m panel tilted 60° projects to 1 m depth: fits a 1.05 m deep strip that the flat panel would not.
        var flat = new SurfaceFrame(0, 180);
        var tilted = PanelFootprintCalculator.Calculate(flat, new PanelOrientation(60, 180), 1, 2, 0, 0);
        var request = new LayoutRequest(1.05, 1.05, 0, 0, 0, [],
            new Dictionary<LayoutOrientation, OrientationCandidate> { [LayoutOrientation.Portrait] = new(tilted, 0) }, 20, 5000);

        var result = PanelLayoutCalculator.Calculate(request);

        Assert.Single(result.Placements);
        Near(1.0, tilted.DepthAcrossRowM);
    }

    [Fact]
    public void Setback_consuming_the_surface_keeps_its_reason()
        => Assert.Equal(NoPanelsReasons.SetbackConsumesSurface, PanelLayoutCalculator.Calculate(Request(2, 2, 1, 2, setback: 1)).NoPanelsReason);
}
