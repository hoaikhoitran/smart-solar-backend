using SmartSolar.Modules.SolarSimulation.Geometry;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// SmartSolar azimuths are compass bearings (0 = North, 90 = East, clockwise).
/// PVGIS "aspect" is south-based (0 = South, -90 = East, 90 = West).
/// </summary>
public class OrientationConventionTests
{
    [Theory]
    [InlineData(180, 0)]
    [InlineData(90, -90)]
    [InlineData(270, 90)]
    [InlineData(135, -45)]
    [InlineData(225, 45)]
    [InlineData(0, -180)]
    [InlineData(360, -180)]
    public void Compass_azimuth_converts_to_pvgis_aspect(double compass, double expectedAspect)
    {
        Near(expectedAspect, AzimuthConvention.ToPvgisAspect(compass));
    }

    [Theory]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(725, 5)]
    [InlineData(180, 180)]
    public void Compass_azimuth_is_normalized_to_zero_inclusive_360_exclusive(double input, double expected)
    {
        Near(expected, AzimuthConvention.NormalizeCompass(input));
    }

    [Fact]
    public void Horizontal_south_facing_surface_maps_x_east_y_south_normal_up()
    {
        var frame = new SurfaceFrame(0, 180);

        Near(new Vec3(1, 0, 0), frame.XAxis);
        Near(new Vec3(0, -1, 0), frame.YAxis);
        Near(new Vec3(0, 0, 1), frame.Normal);
    }

    [Fact]
    public void Inclined_south_facing_surface_slopes_down_toward_south()
    {
        var frame = new SurfaceFrame(30, 180);

        Near(new Vec3(1, 0, 0), frame.XAxis);
        Near(new Vec3(0, -0.866025, -0.5), frame.YAxis);
        Near(new Vec3(0, -0.5, 0.866025), frame.Normal);
        Near(new Vec3(2, -2.598076, -1.5), frame.ToWorld(2, 3, 0));
        Near(new Vec3(0, -0.5, 0.866025), frame.ToWorld(0, 0, 1));
    }

    [Fact]
    public void Horizontal_surface_azimuth_rotates_the_canvas_axes()
    {
        var frame = new SurfaceFrame(0, 90);

        Near(new Vec3(0, 1, 0), frame.XAxis);
        Near(new Vec3(1, 0, 0), frame.YAxis);
        Near(new Vec3(0, 0, 1), frame.Normal);
    }

    [Fact]
    public void Flat_panel_frame_is_the_identity_rotation()
    {
        var panel = new PanelOrientation(0, 180);

        Near(new Vec3(0, 0, 1), panel.Normal);
        Near(new Vec3(1, 0, 0), panel.WidthAxis);
        Near(new Vec3(0, 1, 0), panel.UpAxis);
        var q = panel.ToQuaternion();
        Near(0, q.X); Near(0, q.Y); Near(0, q.Z); Near(1, q.W);
    }

    [Fact]
    public void South_facing_tilted_panel_rotates_about_east_axis()
    {
        var panel = new PanelOrientation(30, 180);

        Near(new Vec3(0, -0.5, 0.866025), panel.Normal);
        Near(new Vec3(1, 0, 0), panel.WidthAxis);
        Near(new Vec3(0, 0.866025, 0.5), panel.UpAxis);
        var q = panel.ToQuaternion();
        Near(0.258819, q.X); Near(0, q.Y); Near(0, q.Z); Near(0.965926, q.W);
    }

    [Fact]
    public void East_facing_tilted_panel_slopes_up_toward_west()
    {
        var panel = new PanelOrientation(20, 90);

        Near(new Vec3(0.342020, 0, 0.939693), panel.Normal);
        Near(new Vec3(0, 1, 0), panel.WidthAxis);
        Near(new Vec3(-0.939693, 0, 0.342020), panel.UpAxis);
    }

    [Fact]
    public void Panel_frame_is_right_handed()
    {
        var panel = new PanelOrientation(37, 211);

        Near(panel.Normal, panel.WidthAxis.Cross(panel.UpAxis));
    }

    [Theory]
    [InlineData(60, 180, 60, 0, true)]   // north-facing panel on a steep south-facing roof faces into it
    [InlineData(20, 180, 30, 180, false)]
    [InlineData(0, 180, 30, 0, false)]   // any orientation is fine on a horizontal surface
    public void Detects_panels_facing_into_the_surface(double surfaceTilt, double surfaceAz, double panelTilt, double panelAz, bool expected)
    {
        Assert.Equal(expected, PanelFootprintCalculator.FacesIntoSurface(new SurfaceFrame(surfaceTilt, surfaceAz), new PanelOrientation(panelTilt, panelAz)));
    }
}
