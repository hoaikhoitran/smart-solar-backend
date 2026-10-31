using System.Diagnostics;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Shading;
using SmartSolar.Tests.TestSupport;
using Xunit.Abstractions;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// Measurements behind two shading decisions, on one SYNTHETIC year of hourly data near Ho Chi Minh City:
/// (1) the 4 × 4 sample grid per panel (the default) against 8 × 8 and 16 × 16, and (2) grouping hours by UTC month (as PVGIS does)
/// instead of the Vietnam local month (UTC+7). The measured numbers are written to the test output.
/// </summary>
public sealed class ShadingResolutionAndMonthTests(ITestOutputHelper output)
{
    private const double Latitude = 10.8;
    private const double Longitude = 106.7;
    private static readonly SurfaceFrame Flat = new(0, 180);
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    private static PanelFace FlatPanel(int index, double x, double y)
        => new(index, Flat.ToWorld(x, y, 0.01), Flat.XAxis, new Vec3(0, 1, 0), Vec3.Up, 1, 1, 500);

    private static IReadOnlyList<PvHourlyRecord> SyntheticYear()
        => FakePvEnergyEstimator.SyntheticHourly(new PvHourlyRequest((decimal)Latitude, (decimal)Longitude, 1m, 14m, 0m, 180m, PvgisMountingPlaces.Building, 2023, 2023))
            .Series!.Records;

    [Fact]
    public void A_4x4_grid_stays_close_to_8x8_and_16x16_at_a_fraction_of_the_cost()
    {
        // A wall east of the panels and a low parapet north of them: partial shadows whose edges cross the panels.
        ObstacleBox[] boxes = [new(5.6, 0, 6.6, 10, 2.5), new(0, 3.0, 10, 3.5, 1)];
        var panels = new[] { FlatPanel(0, 5, 5), FlatPanel(1, 5, 7), FlatPanel(2, 4, 6) };
        var hours = SyntheticYear();

        var results = new Dictionary<int, (double LossFraction, TimeSpan Elapsed)>();
        foreach (var n in new[] { 4, 8, 16 })
        {
            var calculator = new PanelShadingCalculator(new ShadingScene(Flat, 50, 50, boxes, []), panels, n, n, 1.0);
            var watch = Stopwatch.StartNew();
            var result = ShadingEnergyCalculator.Calculate(Latitude, Longitude, [new ShadingSubarray(calculator, hours)]);
            results[n] = (result.AnnualLossFraction, watch.Elapsed);
            output.WriteLine($"{n}x{n}: annual loss {100 * result.AnnualLossFraction:F3} %, {watch.Elapsed.TotalMilliseconds:F0} ms, {calculator.CalculatedBins} direction bins");
        }

        // The finest grid is the reference; the default 4 × 4 stays within 1 percentage point of it.
        Assert.All(results.Values, r => Assert.InRange(r.LossFraction, 0, 1));
        Assert.True(results[16].LossFraction > 0);
        Assert.InRange(Math.Abs(results[4].LossFraction - results[16].LossFraction), 0, 0.01);
        Assert.InRange(Math.Abs(results[8].LossFraction - results[16].LossFraction), 0, 0.01);
    }

    [Fact]
    public void Utc_month_grouping_moves_almost_no_energy_compared_with_vietnam_local_months()
    {
        var hours = SyntheticYear();

        // Energy whose UTC month differs from its local month: only the first local hours (00:00–06:59) of each 1st day.
        var total = hours.Sum(h => h.PowerW);
        var moved = hours.Where(h => h.HourStartUtc.Month != h.HourStartUtc.ToOffset(VietnamOffset).Month).Sum(h => h.PowerW);
        var share = total > 0 ? moved / total : 0;
        output.WriteLine($"energy in a different month under UTC vs UTC+7 grouping: {100 * share:F4} % of the year");

        // Monthly totals under both groupings: the largest relative difference of any month.
        var utc = hours.GroupBy(h => h.HourStartUtc.Month).ToDictionary(g => g.Key, g => g.Sum(h => h.PowerW));
        var local = hours.GroupBy(h => h.HourStartUtc.ToOffset(VietnamOffset).Month).ToDictionary(g => g.Key, g => g.Sum(h => h.PowerW));
        var worst = Enumerable.Range(1, 12).Max(m => utc[m] > 0 ? Math.Abs(utc[m] - local[m]) / utc[m] : 0);
        output.WriteLine($"largest monthly difference: {100 * worst:F4} %");

        Assert.InRange(share, 0, 0.005);    // < 0.5 % of the annual energy
        Assert.InRange(worst, 0, 0.01);     // < 1 % for any month
    }
}
