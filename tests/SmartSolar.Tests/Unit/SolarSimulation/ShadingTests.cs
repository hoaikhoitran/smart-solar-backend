using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Shading;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// The linear irradiance-based shading estimate: sun position, 3D ray casting and the hourly energy formulas. Every hourly
/// value here is SYNTHETIC (chosen to make the expected numbers easy to verify by hand); nothing calls PVGIS.
/// </summary>
public sealed class ShadingTests
{
    // Ho Chi Minh City area.
    private const double Latitude = 10.8;
    private const double Longitude = 106.7;

    /// <summary>A flat surface whose local +Y points south: local +X = east, normal = up.</summary>
    private static readonly SurfaceFrame Flat = new(0, 180);

    /// <summary>A 1 m × 1 m horizontal panel facing up, centred at local (x, y), 1 cm above the surface.</summary>
    private static PanelFace FlatPanel(int index, double x, double y, double ratedW = 500)
        => new(index, Flat.ToWorld(x, y, 0.01), Flat.XAxis, new Vec3(0, 1, 0), Vec3.Up, 1, 1, ratedW);

    private static PanelShadingCalculator Calculator(IReadOnlyList<ObstacleBox> boxes, params PanelFace[] panels)
        => new(new ShadingScene(Flat, 50, 50, boxes, []), panels, 4, 4, 1.0);

    /// <summary>A box enclosing the panel at (5, 5): every ray from the panel starts inside it, so the panel is fully shaded at any sun position.</summary>
    private static readonly ObstacleBox Enclosing = new(0, 0, 10, 10, 5);

    private static PvHourlyRecord Hour(DateTimeOffset startUtc, double powerW, double beam, double diffuse, double reflected)
        => new(startUtc, 10, powerW, beam, diffuse, reflected, 0);

    /// <summary>A UTC hour around local noon (05:00 UTC = 12:00 at UTC+7), when the sun is high.</summary>
    private static readonly DateTimeOffset Noon = new(2023, 3, 21, 5, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ sun position

    [Fact]
    public void Sun_position_matches_the_nrel_spa_reference_case()
    {
        // NREL SPA paper (Reda & Andreas): 2003-10-17 12:30:30 at UTC−7, 39.742476 N, 105.1786 W → zenith 50.11162°, azimuth 194.34024°.
        var sun = SunPositionCalculator.Calculate(39.742476, -105.1786, new DateTimeOffset(2003, 10, 17, 12, 30, 30, TimeSpan.FromHours(-7)));

        Assert.InRange(sun.ElevationDegree, 90 - 50.11162 - 0.05, 90 - 50.11162 + 0.05);
        Assert.InRange(sun.AzimuthDegree, 194.34024 - 0.05, 194.34024 + 0.05);
    }

    [Fact]
    public void Morning_and_noon_sun_differ_and_azimuth_is_clockwise_from_north()
    {
        var morning = SunPositionCalculator.Calculate(Latitude, Longitude, new DateTimeOffset(2023, 3, 21, 7, 0, 0, TimeSpan.FromHours(7)));
        var noon = SunPositionCalculator.Calculate(Latitude, Longitude, new DateTimeOffset(2023, 3, 21, 12, 0, 0, TimeSpan.FromHours(7)));

        Assert.InRange(morning.AzimuthDegree, 80, 100);      // equinox morning: due east
        Assert.InRange(morning.ElevationDegree, 5, 25);
        Assert.True(noon.ElevationDegree > 70);
        // The same instant given in local time or in UTC gives the same position.
        Assert.Equal(noon, SunPositionCalculator.Calculate(Latitude, Longitude, new DateTimeOffset(2023, 3, 21, 5, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Degrees_turn_into_the_world_east_north_up_direction()
    {
        AssertVector(new Vec3(0, 0, 1), new SunPosition(90, 0).Direction);
        AssertVector(new Vec3(1, 0, 0), new SunPosition(0, 90).Direction);
        AssertVector(new Vec3(0, -1, 0), new SunPosition(0, 180).Direction);
        Assert.False(new SunPosition(-3, 180).IsAboveHorizon);
    }

    // ------------------------------------------------------------------ ray casting

    [Fact]
    public void Partial_shading_is_the_blocked_sample_ratio_and_overlapping_obstacles_count_once()
    {
        // A tall box covering the western half of the panel (x 4.5..5.0); the sun straight overhead.
        var half = new ObstacleBox(0, 0, 5.0, 10, 3);
        var overlapping = new ObstacleBox(4.0, 0, 5.0, 10, 3);

        var single = Calculator([half], FlatPanel(0, 5, 5)).RayCast(Vec3.Up);
        var both = Calculator([half, overlapping], FlatPanel(0, 5, 5)).RayCast(Vec3.Up);
        var none = Calculator([], FlatPanel(0, 5, 5)).RayCast(Vec3.Up);

        Assert.Equal(0.5, single[0], 6);
        Assert.Equal(0.5, both[0], 6);   // the overlap is not counted twice
        Assert.Equal(0, none[0]);
    }

    [Fact]
    public void An_east_obstacle_shades_in_the_morning_but_not_at_noon()
    {
        // A 3 m high wall 1 m east of the panel (local +X = east).
        var calculator = Calculator([new ObstacleBox(6, 0, 7, 10, 3)], FlatPanel(0, 5, 5));
        var morning = SunPositionCalculator.Calculate(Latitude, Longitude, new DateTimeOffset(2023, 3, 21, 7, 0, 0, TimeSpan.FromHours(7)));
        var noon = SunPositionCalculator.Calculate(Latitude, Longitude, new DateTimeOffset(2023, 3, 21, 12, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(1, calculator.RayCast(morning.Direction)[0]);
        Assert.Equal(0, calculator.RayCast(noon.Direction)[0]);
    }

    [Fact]
    public void A_rack_row_shades_the_row_behind_it_only_when_the_sun_is_low()
    {
        // Two south-facing 30° rack panels; the front (south) row 1.2 m ahead of the rear row, both 0.5 m above the surface.
        var orientation = new PanelOrientation(30, 180);
        PanelFace Rack(int index, double y) => new(index, Flat.ToWorld(5, y, 0.5), orientation.WidthAxis, orientation.UpAxis, orientation.Normal, 1, 1, 500);
        var front = Rack(0, 6.2);
        var rear = Rack(1, 5.0);
        var calculator = new PanelShadingCalculator(new ShadingScene(Flat, 50, 50, [], [front, rear]), [front, rear], 4, 4, 1.0);

        var lowSouthSun = calculator.RayCast(new SunPosition(10, 180).Direction);
        var highSun = calculator.RayCast(new SunPosition(80, 180).Direction);

        Assert.Equal(0, lowSouthSun[0]);            // nothing in front of the front row
        Assert.True(lowSouthSun[1] > 0);            // the front row shades the rear one
        Assert.Equal(new double[] { 0, 0 }, highSun);
    }

    [Fact]
    public void Below_the_horizon_every_fraction_is_zero()
    {
        var calculator = Calculator([Enclosing], FlatPanel(0, 5, 5));

        Assert.Equal(0, calculator.FractionsFor(new SunPosition(-5, 90))[0]);
        Assert.Equal(1, calculator.FractionsFor(new SunPosition(40, 90))[0]);
    }

    // ------------------------------------------------------------------ energy

    [Fact]
    public void A_full_direct_shadow_removes_only_the_direct_share_and_keeps_diffuse_and_reflected()
    {
        // 1000 W for one hour; Gb 600, Gd 150, Gr 50 W/m² → direct share 0.75.
        var calculator = Calculator([Enclosing], FlatPanel(0, 5, 5));

        var result = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(calculator, [Hour(Noon, 1000, 600, 150, 50)])]);

        Assert.Equal(1.0, result.AnnualEnergyBeforeKwh, 9);     // 1000 W × 1 h / 1000 = 1 kWh
        Assert.Equal(0.75, result.AnnualEnergyLossKwh, 9);      // 4 × 1000 W × 0.75 × 0.25 h / 1000
        Assert.Equal(0.25, result.AnnualEnergyAfterKwh, 9);     // diffuse + reflected remain
        Assert.Equal(1.0, result.AnnualDirectShadedFractionByPanel[0], 9);
    }

    [Fact]
    public void Without_obstacles_or_without_direct_irradiance_there_is_no_loss()
    {
        var open = Calculator([], FlatPanel(0, 5, 5));
        var covered = Calculator([Enclosing], FlatPanel(0, 5, 5));

        var unshaded = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(open, [Hour(Noon, 1000, 600, 150, 50)])]);
        var overcast = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(covered, [Hour(Noon, 400, 0, 300, 20)])]);
        // A row at night (sun below the horizon) with an inconsistent non-zero Gb still loses nothing.
        var night = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(covered, [Hour(Noon.AddHours(13), 100, 200, 0, 0)])]);

        Assert.Equal(0, unshaded.AnnualEnergyLossKwh);
        Assert.Equal((0.4, 0.0), (overcast.AnnualEnergyBeforeKwh, overcast.AnnualEnergyLossKwh));
        Assert.Equal(0, night.AnnualEnergyLossKwh);
    }

    [Fact]
    public void Subarrays_with_different_orientations_are_computed_separately()
    {
        // Subarray A is fully shaded, subarray B is open; each has its own hourly series.
        var a = new ShadingSubarray(Calculator([Enclosing], FlatPanel(0, 5, 5)), [Hour(Noon, 1000, 600, 150, 50)]);
        var b = new ShadingSubarray(Calculator([], FlatPanel(1, 20, 20)), [Hour(Noon, 2000, 900, 80, 20)]);

        var both = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [a, b]);

        Assert.Equal(3.0, both.AnnualEnergyBeforeKwh, 9);
        Assert.Equal(0.75, both.AnnualEnergyLossKwh, 9);    // only A's loss; B is not diluted into A's fraction
        Assert.Equal((1.0, 0.0), (both.AnnualDirectShadedFractionByPanel[0], both.AnnualDirectShadedFractionByPanel[1]));
    }

    [Fact]
    public void Several_years_are_averaged_per_year_and_months_add_up_to_the_year()
    {
        // The same two hours (January and July) in 2021 and in 2022.
        var hours = new[] { 2021, 2022 }.SelectMany(y => new[]
        {
            Hour(new DateTimeOffset(y, 1, 15, 5, 0, 0, TimeSpan.Zero), 1000, 600, 150, 50),
            Hour(new DateTimeOffset(y, 7, 15, 5, 0, 0, TimeSpan.Zero), 2000, 600, 150, 50),
        }).ToList();
        var calculator = Calculator([Enclosing], FlatPanel(0, 5, 5));

        var result = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(calculator, hours)]);

        Assert.Equal(2, result.YearCount);
        Assert.Equal(3.0, result.AnnualEnergyBeforeKwh, 9);   // one year (1 + 2 kWh), not the 6 kWh of both years
        Assert.Equal(result.AnnualEnergyBeforeKwh, result.Monthly.Sum(m => m.EnergyBeforeKwh), 9);
        Assert.Equal(result.AnnualEnergyLossKwh, result.Monthly.Sum(m => m.EnergyLossKwh), 9);
        Assert.Equal((1.0, 2.0), (result.Monthly[0].EnergyBeforeKwh, result.Monthly[6].EnergyBeforeKwh));
    }

    [Fact]
    public void Results_are_finite_non_negative_and_never_above_the_unshaded_baseline()
    {
        // A year of synthetic hours with a partly shading wall and a rack-like neighbour.
        var calculator = Calculator([new ObstacleBox(5.6, 0, 6.6, 10, 2.5), new ObstacleBox(0, 3.0, 10, 3.5, 1)], FlatPanel(0, 5, 5), FlatPanel(1, 5, 7));
        var hours = FakePvHours(2023);

        var result = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(calculator, hours)]);

        Assert.All(result.Monthly, m =>
        {
            Assert.True(double.IsFinite(m.EnergyBeforeKwh) && double.IsFinite(m.EnergyLossKwh));
            Assert.InRange(m.EnergyLossKwh, 0, m.EnergyBeforeKwh);
        });
        Assert.InRange(result.AnnualLossFraction, 0, 1);
        Assert.True(result.AnnualEnergyLossKwh > 0);
        Assert.All(result.AnnualDirectShadedFractionByPanel.Values, f => Assert.InRange(f, 0, 1));
        // The synthetic H_sun is computed with the same algorithm, so the UTC consistency check is exact here.
        Assert.InRange(result.MeanSunHeightDeviationDegree, 0, 1e-9);
    }

    [Fact]
    public void A_year_of_hourly_data_is_ray_cast_once_per_direction_bin_in_bounded_time()
    {
        var calculator = Calculator([new ObstacleBox(5.6, 0, 6.6, 10, 2.5)], Enumerable.Range(0, 20).Select(i => FlatPanel(i, 1 + i * 2.2, 20)).ToArray());
        var hours = FakePvHours(2023);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(calculator, hours)]);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.InRange(calculator.CalculatedBins, 1, 20_000);   // far fewer than the 4 × 8760 sub-samples
        Assert.True(ShadingAnalysis.EstimateWork(calculator, calculator.Scene.OccluderCount, Latitude, Longitude, hours) > 0);
    }

    private static IReadOnlyList<PvHourlyRecord> FakePvHours(int year)
        => FakePvEnergyEstimator.SyntheticHourly(new PvHourlyRequest((decimal)Latitude, (decimal)Longitude, 1m, 14m, 0m, 180m, PvgisMountingPlaces.Building, year, year))
            .Series!.Records;

    private static void AssertVector(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Z, actual.Z, 9);
    }
}
