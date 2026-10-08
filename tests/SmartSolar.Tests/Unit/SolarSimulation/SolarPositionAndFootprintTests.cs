using SmartSolar.Modules.SolarSimulation.Geometry;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class SolarPositionAndFootprintTests
{
    [Fact]
    public void Equinox_noon_on_the_equator_is_at_zenith()
        => Near(new Vec3(0, 0, 1), SolarPosition.SunDirection(0, 0, 0));

    [Fact]
    public void December_solstice_noon_in_ho_chi_minh_city_is_south_at_55_78_degrees()
    {
        // Elevation = 90 - |10.78 - (-23.44)| = 55.78°.
        var sun = SolarPosition.SunDirection(10.78, -23.44, 0);

        Near(0, sun.X);
        Near(-0.56236, sun.Y);
        Near(0.82688, sun.Z);
        Near(55.78, Math.Asin(sun.Z) * 180 / Math.PI, 1e-3);
    }

    [Fact]
    public void Morning_sun_is_in_the_east()
        => Assert.True(SolarPosition.SunDirection(10.78, -23.44, -45).X > 0.6);

    [Fact]
    public void Flush_panel_footprint_is_its_exact_size()
    {
        var footprint = PanelFootprintCalculator.Calculate(
            new SurfaceFrame(30, 180), new PanelOrientation(30, 180),
            physicalWidthM: 1.0, physicalLengthM: 2.0, thicknessM: 0.04, lowEdgeClearanceM: 0);

        Near(1.0, footprint.WidthAlongRowM);
        Near(2.0, footprint.DepthAcrossRowM);
        Near(0, footprint.RowRotationDegree);
        Assert.False(footprint.IsConservativeBoundingBox);
        Near(0.04, footprint.FrontCenterHeightM);
        Near(0, footprint.LowEdgeClearanceM);
        Near(0.04, footprint.HighEdgeHeightM);
    }

    [Fact]
    public void Rack_panel_on_flat_surface_projects_its_length_by_cosine_tilt()
    {
        var footprint = PanelFootprintCalculator.Calculate(
            new SurfaceFrame(0, 180), new PanelOrientation(30, 180),
            physicalWidthM: 1.0, physicalLengthM: 2.0, thicknessM: 0, lowEdgeClearanceM: 0.2);

        Near(1.0, footprint.WidthAlongRowM);
        Near(1.732051, footprint.DepthAcrossRowM);
        Near(0, footprint.RowRotationDegree);
        Near(0.2, footprint.LowEdgeClearanceM);
        Near(1.2, footprint.HighEdgeHeightM);
        Near(0.7, footprint.FrontCenterHeightM);
        Assert.False(footprint.IsConservativeBoundingBox);
    }

    [Fact]
    public void Module_thickness_enlarges_the_rack_footprint()
    {
        // Back face shifts by thickness * sin(tilt) across the row: 2cos30 + 0.04 sin30.
        var footprint = PanelFootprintCalculator.Calculate(
            new SurfaceFrame(0, 180), new PanelOrientation(30, 180),
            physicalWidthM: 1.0, physicalLengthM: 2.0, thicknessM: 0.04, lowEdgeClearanceM: 0.2);

        Near(1.752051, footprint.DepthAcrossRowM);
    }

    [Fact]
    public void Panel_azimuth_is_independent_of_horizontal_surface_azimuth()
    {
        var footprint = PanelFootprintCalculator.Calculate(
            new SurfaceFrame(0, 180), new PanelOrientation(30, 90),
            physicalWidthM: 1.0, physicalLengthM: 2.0, thicknessM: 0, lowEdgeClearanceM: 0);

        Near(-90, footprint.RowRotationDegree);
        Near(1.0, footprint.WidthAlongRowM);
        Near(1.732051, footprint.DepthAcrossRowM);
        Assert.False(footprint.IsConservativeBoundingBox);
    }

    [Theory]
    [InlineData(180, false)]
    [InlineData(120, true)]
    public void Rack_on_inclined_surface_is_conservative_only_when_azimuths_differ(double panelAzimuth, bool conservative)
    {
        var footprint = PanelFootprintCalculator.Calculate(
            new SurfaceFrame(20, 180), new PanelOrientation(30, panelAzimuth),
            physicalWidthM: 1.0, physicalLengthM: 2.0, thicknessM: 0.04, lowEdgeClearanceM: 0.2);

        Assert.Equal(conservative, footprint.IsConservativeBoundingBox);
        Near(0.2, footprint.LowEdgeClearanceM);
    }

    [Fact]
    public void Panel_corners_never_sit_below_the_low_edge_clearance()
    {
        // A panel flatter than the roof must be lifted on its low side.
        var surface = new SurfaceFrame(35, 180);
        var footprint = PanelFootprintCalculator.Calculate(
            surface, new PanelOrientation(10, 180), 1.0, 2.0, 0.04, 0.1);

        var minHeight = footprint.SolidCorners.Min(c => c.Dot(surface.Normal)) + footprint.FrontCenterHeightM;
        Near(0.1, minHeight);
        Assert.True(footprint.HighEdgeHeightM > 0.1);
    }

    [Fact]
    public void Shade_free_gap_is_zero_for_flush_panels()
    {
        var surface = new SurfaceFrame(15, 180);
        var footprint = PanelFootprintCalculator.Calculate(surface, new PanelOrientation(15, 180), 1.0, 2.0, 0.04, 0);

        var gap = ShadeFreeRowGapCalculator.Calculate(10.78, surface, footprint, ShadingDesignWindow.Default);

        Assert.True(gap < 0.05, $"Flush panels cast almost no shadow beyond their thickness, got {gap}.");
    }

    [Fact]
    public void Shade_free_gap_grows_with_tilt_and_latitude()
    {
        var flat = new SurfaceFrame(0, 180);
        double Gap(double latitude, double tilt)
        {
            var footprint = PanelFootprintCalculator.Calculate(flat, new PanelOrientation(tilt, 180), 1.0, 2.0, 0, 0);
            return ShadeFreeRowGapCalculator.Calculate(latitude, flat, footprint, ShadingDesignWindow.Default);
        }

        Assert.True(Gap(10.78, 10) > 0);
        Assert.True(Gap(10.78, 30) > Gap(10.78, 10));
        Assert.True(Gap(45, 30) > Gap(10.78, 30));
    }

    [Fact]
    public void Shade_free_gap_matches_the_hand_computed_noon_shadow_when_the_window_is_noon_only()
    {
        // Panel 2 m long tilted 30° on flat ground, low edge on the ground, sun at 55.78° elevation due south.
        // Top edge height 1.0 m; its shadow falls 1.0 / tan(55.78°) = 0.68034 m north of the top edge.
        var flat = new SurfaceFrame(0, 180);
        var footprint = PanelFootprintCalculator.Calculate(flat, new PanelOrientation(30, 180), 1.0, 2.0, 0, 0);
        var noonOnly = new ShadingDesignWindow([-23.44], StartSolarHour: 12, EndSolarHour: 12, StepMinutes: 15);

        var gap = ShadeFreeRowGapCalculator.Calculate(10.78, flat, footprint, noonOnly);

        Near(0.68034, gap, 1e-3);
    }
}
