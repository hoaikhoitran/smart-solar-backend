using SmartSolar.Modules.SolarSimulation.Geometry;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class AreaAndPolygonTests
{
    [Fact]
    public void Union_of_disjoint_rectangles_is_their_sum()
        => Near(4, RectUnion.Area([new Rect(0, 0, 2, 1), new Rect(3, 0, 4, 2)]));

    [Fact]
    public void Overlap_is_counted_once()
        => Near(7, RectUnion.Area([new Rect(0, 0, 2, 2), new Rect(1, 1, 3, 3)]));

    [Fact]
    public void Contained_rectangle_adds_nothing()
        => Near(16, RectUnion.Area([new Rect(0, 0, 4, 4), new Rect(1, 1, 2, 2)]));

    [Fact]
    public void Touching_rectangles_do_not_overlap()
        => Near(2, RectUnion.Area([new Rect(0, 0, 1, 1), new Rect(1, 0, 2, 1)]));

    [Fact]
    public void Area_within_a_region_clips_outside_parts()
        => Near(1, RectUnion.AreaWithin(new Rect(0, 0, 10, 10), [new Rect(-1, -1, 1, 1)]));

    [Fact]
    public void Empty_input_has_no_area()
        => Near(0, RectUnion.Area([]));

    private static ConvexPolygon Diamond()
        => new([new Vec2(0, -2), new Vec2(2, 0), new Vec2(0, 2), new Vec2(-2, 0)]);

    [Theory]
    [InlineData(0, -2, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(-1.5, -0.5, 0.5)]
    public void Horizontal_line_crosses_convex_polygon(double y, double expectedMin, double expectedMax)
    {
        var range = Diamond().XRangeAt(y);

        Assert.NotNull(range);
        Near(expectedMin, range.Value.Min);
        Near(expectedMax, range.Value.Max);
    }

    [Fact]
    public void Horizontal_line_outside_polygon_has_no_range()
        => Assert.Null(Diamond().XRangeAt(3));

    [Fact]
    public void Clipping_to_a_band_keeps_only_the_band()
    {
        var clipped = Diamond().ClipToBand(-1, 1);

        Assert.False(clipped.IsEmpty);
        Near(-2, clipped.MinX); Near(2, clipped.MaxX);
        Near(-1, clipped.MinY); Near(1, clipped.MaxY);
    }

    [Fact]
    public void A_band_only_touching_a_vertex_is_empty()
        => Assert.True(Diamond().ClipToBand(2, 3).IsEmpty);

    [Fact]
    public void Rotated_rectangle_polygon_has_the_rotated_extent()
    {
        var polygon = ConvexPolygon.FromRect(new Rect(0, 0, 2, 1)).Map(p => new Vec2(-p.Y, p.X));

        Near(-1, polygon.MinX); Near(0, polygon.MaxX);
        Near(0, polygon.MinY); Near(2, polygon.MaxY);
    }
}
