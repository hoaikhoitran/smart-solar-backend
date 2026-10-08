using SmartSolar.Modules.SolarSimulation.Geometry;
using static SmartSolar.Tests.Unit.SolarSimulation.GeometryAssert;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public class PanelLayoutCalculatorTests
{
    /// <summary>A flat footprint with no rotation: width along X, depth along Y.</summary>
    private static PanelFootprint Box(double width, double depth)
        => PanelFootprintCalculator.Calculate(new SurfaceFrame(0, 180), new PanelOrientation(0, 180), width, depth, 0, 0);

    private static LayoutRequest Request(
        double surfaceWidth, double surfaceLength,
        double panelShort = 1, double panelLong = 2,
        double gap = 0, double rowGap = 0, double setback = 0, double clearance = 0,
        IReadOnlyList<LayoutObstacle>? obstacles = null, int maxPlacements = 5000)
        => new(
            surfaceWidth, surfaceLength, setback, clearance, gap, obstacles ?? [],
            new Dictionary<LayoutOrientation, OrientationCandidate>
            {
                [LayoutOrientation.Portrait] = new(Box(panelShort, panelLong), rowGap),
                [LayoutOrientation.Landscape] = new(Box(panelLong, panelShort), rowGap),
            },
            OffsetSamples: 20,
            MaxPlacements: maxPlacements);

    [Fact]
    public void Chooses_landscape_when_it_fits_more_panels()
    {
        // 10 x 5 m: portrait 10 per row x 2 rows = 20; landscape 5 x 5 = 25.
        var result = PanelLayoutCalculator.Calculate(Request(10, 5));

        Assert.Equal(LayoutOrientation.Landscape, result.Orientation);
        Assert.Equal(25, result.Placements.Count);
    }

    [Fact]
    public void Panel_gap_and_row_gap_reduce_the_count()
    {
        // Portrait: floor(10.02 / 1.02) = 9 per row, floor(5.02 / 2.02) = 2 rows -> 18.
        // Landscape: floor(10.02 / 2.02) = 4 per row, floor(5.02 / 1.02) = 4 rows -> 16.
        var result = PanelLayoutCalculator.Calculate(Request(10, 5, gap: 0.02, rowGap: 0.02));

        Assert.Equal(LayoutOrientation.Portrait, result.Orientation);
        Assert.Equal(18, result.Placements.Count);
    }

    [Fact]
    public void Edge_setback_shrinks_the_installable_region()
    {
        // 9 x 4 m region: portrait 9 x 2 = 18 beats landscape 4 x 4 = 16.
        var result = PanelLayoutCalculator.Calculate(Request(10, 5, setback: 0.5));

        Assert.Equal(18, result.Placements.Count);
        Assert.All(result.Placements, p =>
        {
            Assert.True(p.MinXM >= 0.5 - 1e-9 && p.MaxXM <= 9.5 + 1e-9);
            Assert.True(p.MinYM >= 0.5 - 1e-9 && p.MaxYM <= 4.5 + 1e-9);
        });
    }

    [Fact]
    public void Obstacle_position_matters_not_just_its_area()
    {
        // 4 x 2 m with a 1 x 2 m obstacle in the middle. Area formula would give (8-2)/2 = 3;
        // geometrically only one portrait panel fits on each side.
        var result = PanelLayoutCalculator.Calculate(Request(4, 2, obstacles: [new LayoutObstacle(1.5, 0, 1, 2)]));

        Assert.Equal(2, result.Placements.Count);
        Assert.Equal(LayoutOrientation.Portrait, result.Orientation);
    }

    [Theory]
    [InlineData(0.25, 2)]
    [InlineData(0.5, 2)]
    [InlineData(0.6, 0)]
    public void Obstacle_clearance_is_applied_around_the_obstacle(double clearance, int expected)
    {
        var result = PanelLayoutCalculator.Calculate(
            Request(4, 2, clearance: clearance, obstacles: [new LayoutObstacle(1.5, 0, 1, 2)]));

        Assert.Equal(expected, result.Placements.Count);
    }

    [Fact]
    public void Panels_may_touch_an_obstacle_on_the_boundary()
    {
        var result = PanelLayoutCalculator.Calculate(Request(3, 2, obstacles: [new LayoutObstacle(0, 0, 1, 2)]));

        Assert.Equal(2, result.Placements.Count);
        Near(1, result.Placements.Min(p => p.MinXM));
    }

    [Fact]
    public void Row_offset_search_recovers_space_below_a_thin_obstacle()
    {
        // 1 x 3.2 m with a 0.2 m strip at the top: starting rows at 0.2 fits 3 squares, starting at 0 fits 2.
        var result = PanelLayoutCalculator.Calculate(
            Request(1, 3.2, panelShort: 1, panelLong: 1, obstacles: [new LayoutObstacle(0, 0, 1, 0.2)]));

        Assert.Equal(3, result.Placements.Count);
    }

    [Fact]
    public void Panel_larger_than_the_surface_gives_zero_with_reason()
    {
        var result = PanelLayoutCalculator.Calculate(Request(1, 1, panelShort: 1.2, panelLong: 2));

        Assert.Empty(result.Placements);
        Assert.Null(result.Orientation);
        Assert.Equal(NoPanelsReasons.PanelLargerThanInstallableRegion, result.NoPanelsReason);
    }

    [Fact]
    public void Setback_consuming_the_surface_gives_zero_with_reason()
    {
        var result = PanelLayoutCalculator.Calculate(Request(2, 2, setback: 1));

        Assert.Empty(result.Placements);
        Assert.Equal(NoPanelsReasons.SetbackConsumesSurface, result.NoPanelsReason);
        Near(0, result.Areas.InstallableAreaM2);
    }

    [Fact]
    public void Obstacle_covering_everything_gives_zero_with_reason()
    {
        var result = PanelLayoutCalculator.Calculate(Request(2, 2, obstacles: [new LayoutObstacle(0, 0, 2, 2)]));

        Assert.Empty(result.Placements);
        Assert.Equal(NoPanelsReasons.ObstaclesBlockAllPositions, result.NoPanelsReason);
        Near(4, result.Areas.ObstacleOccupiedAreaM2);
        Near(0, result.Areas.AvailableSurfaceAreaM2);
    }

    [Fact]
    public void Areas_use_union_of_obstacles_and_clearance()
    {
        // Obstacles 2x2 overlapping by 1 m²: occupied 7. With 0.5 m clearance the grown
        // squares are 3x3 overlapping 2x2: 9 + 9 - 4 = 14, so installable = 50 - 14 = 36.
        var obstacles = new[] { new LayoutObstacle(1, 1, 2, 2), new LayoutObstacle(2, 2, 2, 2) };

        var result = PanelLayoutCalculator.Calculate(Request(10, 5, clearance: 0.5, obstacles: obstacles));

        Near(50, result.Areas.GrossSurfaceAreaM2);
        Near(7, result.Areas.ObstacleOccupiedAreaM2);
        Near(43, result.Areas.AvailableSurfaceAreaM2);
        Near(36, result.Areas.InstallableAreaM2);
        Near(result.Placements.Count * 2.0, result.Areas.PanelCoveredAreaM2);
        Assert.True(result.Areas.PanelCoveredAreaM2 <= result.Areas.InstallableAreaM2 + 1e-9);
    }

    [Fact]
    public void Rotated_rows_on_a_square_surface_fit_the_same_count()
    {
        var flat = new SurfaceFrame(0, 180);
        var eastFacing = PanelFootprintCalculator.Calculate(flat, new PanelOrientation(30, 90), 1, 2, 0, 0);
        var request = new LayoutRequest(10, 10, 0, 0, 0, [],
            new Dictionary<LayoutOrientation, OrientationCandidate> { [LayoutOrientation.Portrait] = new(eastFacing, 0) },
            20, 5000);

        var result = PanelLayoutCalculator.Calculate(request);

        // 10 per row x floor(10 / 1.732) = 5 rows.
        Assert.Equal(50, result.Placements.Count);
        AssertNoOverlapsAndInside(result, request);
    }

    [Fact]
    public void Layout_is_deterministic()
    {
        var obstacles = new[] { new LayoutObstacle(2.3, 1.1, 1.7, 0.9), new LayoutObstacle(6, 3, 1.5, 2) };
        var first = PanelLayoutCalculator.Calculate(Request(11, 7, gap: 0.02, rowGap: 0.3, setback: 0.3, clearance: 0.3, obstacles: obstacles));
        var second = PanelLayoutCalculator.Calculate(Request(11, 7, gap: 0.02, rowGap: 0.3, setback: 0.3, clearance: 0.3, obstacles: obstacles));

        Assert.Equal(first.Orientation, second.Orientation);
        Assert.Equal(first.Placements, second.Placements);
    }

    [Fact]
    public void Exceeding_the_placement_limit_is_reported()
    {
        var result = PanelLayoutCalculator.Calculate(Request(10, 10, maxPlacements: 10));

        Assert.True(result.ExceededMaxPlacements);
    }

    [Fact]
    public void Generated_layouts_never_overlap_and_stay_inside_with_clearance()
    {
        var random = new Random(20261008);
        for (var i = 0; i < 40; i++)
        {
            var width = 3 + random.NextDouble() * 12;
            var length = 3 + random.NextDouble() * 12;
            var obstacles = Enumerable.Range(0, random.Next(0, 5)).Select(_ =>
            {
                var w = 0.2 + random.NextDouble() * 2;
                var l = 0.2 + random.NextDouble() * 2;
                return new LayoutObstacle(random.NextDouble() * (width - w), random.NextDouble() * (length - l), w, l);
            }).ToList();
            var request = Request(width, length, panelShort: 1.134, panelLong: 2.278,
                gap: 0.02, rowGap: random.NextDouble() * 0.5, setback: random.NextDouble() * 0.5,
                clearance: random.NextDouble() * 0.5, obstacles: obstacles);

            var result = PanelLayoutCalculator.Calculate(request);

            AssertNoOverlapsAndInside(result, request);
        }
    }

    // ---------- Computational bounds ----------

    private static LayoutRequest Squares(double width, double length, double side, int maxPlacements, IReadOnlyList<LayoutObstacle>? obstacles = null)
        => Request(width, length, panelShort: side, panelLong: side, obstacles: obstacles, maxPlacements: maxPlacements);

    private static (LayoutResult Result, TimeSpan Elapsed, long AllocatedBytes) Measure(LayoutRequest request)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = PanelLayoutCalculator.Calculate(request);
        return (result, watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Exactly_the_placement_limit_is_allowed()
    {
        // 50 x 100 m of 1 m squares: 5000 panels in either orientation.
        var result = PanelLayoutCalculator.Calculate(Squares(50, 100, 1, 5000));

        Assert.False(result.ExceededMaxPlacements);
        Assert.False(result.ExceededComputationBudget);
        Assert.Equal(5000, result.Placements.Count);
    }

    [Fact]
    public void One_panel_over_the_limit_is_reported_without_a_truncated_layout()
    {
        var result = PanelLayoutCalculator.Calculate(Squares(50, 100, 1, 4999));

        Assert.True(result.ExceededMaxPlacements);
        Assert.Empty(result.Placements);
        Assert.Null(result.Orientation);
        Assert.Null(result.NoPanelsReason);
    }

    [Fact]
    public void Far_more_feasible_panels_than_the_limit_stops_early()
    {
        var (result, elapsed, allocated) = Measure(Squares(100, 100, 1, 5000));

        Assert.True(result.ExceededMaxPlacements);
        Assert.Empty(result.Placements);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Took {elapsed}.");
        Assert.True(allocated < 100_000_000, $"Allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Tiny_panels_on_a_200_m_square_surface_terminate_quickly_with_bounded_memory()
    {
        var obstacles = Enumerable.Range(0, 50).Select(i => new LayoutObstacle(i * 3.9, i * 3.9, 1, 1)).ToList();

        var (result, elapsed, allocated) = Measure(Squares(200, 200, 0.05, 5000, obstacles));

        Assert.True(result.ExceededMaxPlacements || result.ExceededComputationBudget);
        Assert.Empty(result.Placements);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Took {elapsed}.");
        Assert.True(allocated < 200_000_000, $"Allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Panel_far_narrower_than_the_span_does_not_overflow_into_zero()
    {
        // 200 m / 1e-12 m would overflow a 32-bit count; the limit must still be reported.
        var result = PanelLayoutCalculator.Calculate(Request(200, 10, panelShort: 1e-12, panelLong: 5, maxPlacements: 5000));

        Assert.True(result.ExceededMaxPlacements);
        Assert.NotEqual(NoPanelsReasons.PanelLargerThanInstallableRegion, result.NoPanelsReason);
    }

    [Fact]
    public void Extremely_thin_rows_exceed_the_computation_budget_instead_of_looping()
    {
        // 1 µm deep rows over 200 m would be 200 million row iterations per offset.
        var (result, elapsed, _) = Measure(Request(1.5, 200, panelShort: 1e-6, panelLong: 1.2, maxPlacements: int.MaxValue));

        Assert.True(result.ExceededComputationBudget || result.ExceededMaxPlacements);
        Assert.Empty(result.Placements);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Took {elapsed}.");
    }

    [Fact]
    public void Large_obstacle_heavy_surface_with_real_panels_completes_within_budget()
    {
        var obstacles = Enumerable.Range(0, 50).Select(i => new LayoutObstacle((i % 10) * 19.5 + 2, (i / 10) * 39 + 3, 1.5, 2)).ToList();

        var (result, elapsed, _) = Measure(Request(200, 200, panelShort: 1.134, panelLong: 2.278,
            gap: 0.02, rowGap: 0.02, setback: 0.3, clearance: 0.3, obstacles: obstacles, maxPlacements: 50_000));

        Assert.False(result.ExceededComputationBudget);
        Assert.False(result.ExceededMaxPlacements);
        Assert.True(result.Placements.Count > 10_000);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed}.");
        AssertNoOverlapsAndInside(result, Request(200, 200, panelShort: 1.134, panelLong: 2.278,
            gap: 0.02, rowGap: 0.02, setback: 0.3, clearance: 0.3, obstacles: obstacles, maxPlacements: 50_000));
    }

    [Fact]
    public void Zero_panel_layouts_are_not_limit_errors()
    {
        var result = PanelLayoutCalculator.Calculate(Request(1, 1, panelShort: 1.2, panelLong: 2));

        Assert.False(result.ExceededMaxPlacements);
        Assert.False(result.ExceededComputationBudget);
        Assert.Equal(NoPanelsReasons.PanelLargerThanInstallableRegion, result.NoPanelsReason);
    }

    private static void AssertNoOverlapsAndInside(LayoutResult result, LayoutRequest request)
    {
        const double eps = 1e-7;
        var placements = result.Placements;
        var theta = result.Orientation is { } o ? request.Candidates[o].Footprint.RowRotationDegree * Math.PI / 180 : 0;

        // Corners of each footprint (local frame) must lie inside the setback region.
        foreach (var p in placements)
        {
            foreach (var corner in p.CornersLocal)
            {
                Assert.InRange(corner.X, request.EdgeSetbackM - eps, request.SurfaceWidthM - request.EdgeSetbackM + eps);
                Assert.InRange(corner.Y, request.EdgeSetbackM - eps, request.SurfaceLengthM - request.EdgeSetbackM + eps);
            }
        }

        // In the row frame footprints are axis-aligned boxes: they must not overlap each other
        // and must not overlap any clearance-grown obstacle polygon.
        var rowBoxes = placements.Select(p => (p.MinS, p.MinT, p.MaxS, p.MaxT)).ToList();
        for (var a = 0; a < rowBoxes.Count; a++)
        {
            for (var b = a + 1; b < rowBoxes.Count; b++)
            {
                var overlapS = Math.Min(rowBoxes[a].MaxS, rowBoxes[b].MaxS) - Math.Max(rowBoxes[a].MinS, rowBoxes[b].MinS);
                var overlapT = Math.Min(rowBoxes[a].MaxT, rowBoxes[b].MaxT) - Math.Max(rowBoxes[a].MinT, rowBoxes[b].MinT);
                Assert.False(overlapS > eps && overlapT > eps, $"Panels {a} and {b} overlap.");
            }
        }

        foreach (var obstacle in request.Obstacles)
        {
            var grown = new Rect(obstacle.X - request.ObstacleClearanceM, obstacle.Y - request.ObstacleClearanceM,
                obstacle.X + obstacle.Width + request.ObstacleClearanceM, obstacle.Y + obstacle.Length + request.ObstacleClearanceM);
            var polygon = ConvexPolygon.FromRect(grown).Map(v => new Vec2(
                v.X * Math.Cos(theta) + v.Y * Math.Sin(theta), -v.X * Math.Sin(theta) + v.Y * Math.Cos(theta)));
            foreach (var box in rowBoxes)
            {
                var clipped = polygon.ClipToBand(box.MinT + eps, box.MaxT - eps);
                if (clipped.IsEmpty) continue;
                Assert.False(clipped.MaxX > box.MinS + eps && clipped.MinX < box.MaxS - eps, "Panel overlaps an obstacle clearance zone.");
            }
        }
    }
}
