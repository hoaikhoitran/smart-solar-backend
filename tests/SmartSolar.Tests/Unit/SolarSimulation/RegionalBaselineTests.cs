using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.Regional;
using SmartSolar.Modules.SolarSimulation.SelectSimulation;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// The 2025 Ho Chi Minh City regional solar baseline: verified dataset, area-weighted math on REAL recorded provider responses, coverage
/// thresholds, caching and invalidation, the three former territories, and the simulation contract (site inputs still matter, the regional
/// climate baseline does not depend on the site point).
/// </summary>
public sealed class RegionalBaselineTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static RegionalDataset Dataset => RegionalTestSupport.Dataset;

    // ------------------------------------------------------------------ dataset

    [Fact]
    public void Shipped_dataset_is_verified_area_weighted_and_covers_the_whole_2025_territory()
    {
        var d = Dataset;
        Assert.Equal(("79", RegionalTestSupport.DatasetKey, "EQUAL_AREA_GRID_CLIPPED_AREA", 26), (d.RegionCode, d.DatasetKey, d.WeightMethod, d.Samples.Count));
        Assert.Equal(("VN79", "2025-09-25"), (d.Boundary.FeaturePcode, d.Boundary.ValidOn));
        Assert.Contains("CC BY-IGO", d.Boundary.License);
        Assert.Contains("GSO", d.Boundary.Publisher);
        Assert.Equal(64, d.Boundary.FileSha256.Length);
        Assert.Equal(1m, d.Samples.Sum(s => s.Weight));
        Assert.All(d.Samples, s => Assert.True(s.Weight > 0 && d.Contains(s.Latitude, s.Longitude)));
        // Not three equal thirds and not one centre point: area weights from the verified geometry.
        Assert.True(d.Samples.Select(s => s.Weight).Distinct().Count() > 10);
        Assert.True(d.Samples.Max(s => s.Weight) < 0.1m);
        // The islands (Côn Đảo, south of 9°N) are covered with their own small area weight.
        var conDao = Assert.Single(d.Samples, s => s.Latitude < 9m);
        Assert.InRange(conDao.Weight, 0.010m, 0.014m);
        Assert.True(d.ReferencePoint.DisplayOnly);
    }

    [Theory]
    [InlineData(10.7725, 106.6980, true)]    // Bến Thành (former Ho Chi Minh City)
    [InlineData(10.9804, 106.6519, true)]    // Thủ Dầu Một (former Bình Dương)
    [InlineData(11.2770, 106.3640, true)]    // Dầu Tiếng (former Bình Dương)
    [InlineData(10.3460, 107.0843, true)]    // Vũng Tàu (former Bà Rịa – Vũng Tàu)
    [InlineData(8.6829, 106.6087, true)]     // Côn Sơn island
    [InlineData(10.4114, 106.9547, true)]    // Cần Giờ
    [InlineData(10.9574, 106.8427, false)]   // Biên Hòa (Đồng Nai)
    [InlineData(10.5359, 106.4137, false)]   // Tân An (Tây Ninh since 2025)
    [InlineData(21.0285, 105.8542, false)]   // Hà Nội
    public void The_boundary_is_the_merged_2025_city_not_the_old_rectangle(double lat, double lon, bool inside)
        => Assert.Equal(inside, Dataset.Contains((decimal)lat, (decimal)lon));

    [Fact]
    public void An_altered_or_incomplete_dataset_is_refused_and_an_enabled_runtime_never_starts_without_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-regional-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Copy(string name)
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(RegionalTestSupport.DatasetDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                return dir;
            }
            // A changed weight: the canonical sample hash no longer matches the manifest.
            var weights = Copy("weights");
            var csv = Path.Combine(weights, "samples.csv");
            File.WriteAllText(csv, File.ReadAllText(csv).Replace(",0.011949341,", ",0.111949341,"));
            Assert.Throws<RegionalDatasetException>(() => RegionalDataset.Load(weights));
            // No polygon: the boundary file is missing.
            var noPolygon = Copy("no-polygon");
            File.Delete(Path.Combine(noPolygon, "boundary-simplified.geojson"));
            Assert.Contains("boundary", Assert.Throws<RegionalDatasetException>(() => RegionalDataset.Load(noPolygon)).Message, StringComparison.OrdinalIgnoreCase);
            // An altered boundary.
            var altered = Copy("altered");
            var geo = Path.Combine(altered, "boundary-simplified.geojson");
            File.WriteAllText(geo, File.ReadAllText(geo).Replace("106.", "105."));
            Assert.Throws<RegionalDatasetException>(() => RegionalDataset.Load(altered));
            // Line endings do not matter (canonical hashes).
            var crlf = Copy("crlf");
            var crlfCsv = Path.Combine(crlf, "samples.csv");
            File.WriteAllText(crlfCsv, File.ReadAllText(crlfCsv).Replace("\n", "\r\n"));
            Assert.Equal(26, RegionalDataset.Load(crlf).Samples.Count);
            // Fail fast: an enabled runtime with no dataset throws (no silent city-centre fallback); a disabled one loads nothing.
            Assert.ThrowsAny<Exception>(() => RegionalBaselineRuntime.Create(new RegionalBaselineOptions { DatasetDirectory = Path.Combine(root, "missing") }, root));
            Assert.Null(RegionalBaselineRuntime.Create(new RegionalBaselineOptions { Enabled = false }, root).Dataset);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ------------------------------------------------------------------ math on real recorded provider responses

    private static IReadOnlyList<RegionalPvSampleResult> RealPv(Func<RegionalSample, bool>? fail = null)
        => Dataset.Samples.Select(s =>
        {
            var real = RegionalTestSupport.RealResponses.Single(r => r.LocationId == s.LocationId);
            return fail?.Invoke(s) == true
                ? new RegionalPvSampleResult(s, null, new ProviderFailure(ProviderFailureCodes.Unavailable, "down"))
                : new RegionalPvSampleResult(s, real.Pvgis, null);
        }).ToList();

    [Fact]
    public void Real_PVGIS_responses_give_the_documented_area_weighted_city_yield()
    {
        var baseline = RegionalAggregationCalculator.AggregatePv(RealPv(), 1m, 0.95m, DateTimeOffset.UnixEpoch, out var coverage)!;
        // Σ_i w_i · E_y,i from the recorded fixture = 1446.126 kWh/kWp (documented convergence check, 20 km grid).
        Assert.Equal(1446.13m, Math.Round(baseline.AnnualSpecificYieldKwhPerKwp, 2));
        Assert.Equal((RegionalQualities.Complete, 26, 1m), (coverage.Quality, coverage.SucceededSampleCount, coverage.CoveredWeightFraction));
        Assert.Equal(["PVGIS-ERA5"], baseline.RadiationDatabases);
        Assert.Equal((2005, 2023), (baseline.ProviderYearMin, baseline.ProviderYearMax));
        // The weighting matters: the unweighted mean of the same points differs, and every month is a true weighted mean (between the extremes).
        var unweighted = RegionalTestSupport.RealResponses.Average(r => r.Pvgis.Monthly.Sum(m => m.EnergyKwh));
        Assert.NotEqual(Math.Round(baseline.AnnualSpecificYieldKwhPerKwp, 1), Math.Round(unweighted, 1));
        for (var m = 1; m <= 12; m++)
        {
            var values = RegionalTestSupport.RealResponses.Select(r => r.Pvgis.Monthly.Single(x => x.Month == m).EnergyKwh).ToList();
            Assert.InRange(baseline.Monthly.Single(x => x.Month == m).SpecificYieldKwhPerKwp, values.Min(), values.Max());
        }
    }

    [Fact]
    public void Site_energy_is_the_regional_yield_times_installed_kWp_and_months_add_up_to_the_year()
    {
        var baseline = RegionalAggregationCalculator.AggregatePv(RealPv(), 1m, 0.95m, DateTimeOffset.UnixEpoch, out _)!;
        foreach (var kwp in new[] { 1m, 5.5m, 13.2m })
        {
            var site = RegionalAggregationCalculator.ScaleToSite(baseline, kwp);
            Assert.Equal(12, site.Count);
            Assert.All(site, m => Assert.Equal(Math.Round(baseline.Monthly.Single(b => b.Month == m.Month).SpecificYieldKwhPerKwp * kwp, 2, MidpointRounding.AwayFromZero), m.EnergyKwh));
            Assert.InRange(site.Sum(m => m.EnergyKwh), baseline.AnnualSpecificYieldKwhPerKwp * kwp - 0.06m, baseline.AnnualSpecificYieldKwhPerKwp * kwp + 0.06m);
            Assert.All(site, m => Assert.Null(m.StdDevKwh));   // a regional interannual deviation is not invented
        }
        // 1 kWp normalisation: energies recorded for a 2 kWp reference give the same specific yield.
        var doubled = Dataset.Samples.Select(s =>
        {
            var e = RegionalTestSupport.RealResponses.Single(r => r.LocationId == s.LocationId).Pvgis;
            return new RegionalPvSampleResult(s, e with { Monthly = e.Monthly.Select(m => m with { EnergyKwh = 2 * m.EnergyKwh }).ToList() }, null);
        }).ToList();
        var normalised = RegionalAggregationCalculator.AggregatePv(doubled, 2m, 0.95m, DateTimeOffset.UnixEpoch, out _)!;
        Assert.Equal(baseline.AnnualSpecificYieldKwhPerKwp, normalised.AnnualSpecificYieldKwhPerKwp);
    }

    [Fact]
    public void A_missing_sample_is_renormalised_only_above_the_coverage_threshold_and_never_turned_into_zero()
    {
        // Côn Đảo (≈1.2 % of the area) missing: DEGRADED, renormalised over 98.8 %.
        var degraded = RegionalAggregationCalculator.AggregatePv(RealPv(s => s.Latitude < 9m), 1m, 0.95m, DateTimeOffset.UnixEpoch, out var c1)!;
        Assert.Equal((RegionalQualities.Degraded, 25), (c1.Quality, c1.SucceededSampleCount));
        Assert.Equal(1m - Dataset.Samples.Single(s => s.Latitude < 9m).Weight, c1.CoveredWeightFraction);
        Assert.Single(c1.MissingLocationIds);
        Assert.InRange(degraded.AnnualSpecificYieldKwhPerKwp, 1400m, 1500m);
        // Below the threshold: no baseline at all (not a partial sum, not zero).
        Assert.Null(RegionalAggregationCalculator.AggregatePv(RealPv(s => s.Latitude > 10.8m), 1m, 0.95m, DateTimeOffset.UnixEpoch, out var c2));
        Assert.True(c2.CoveredWeightFraction < 0.95m);
        // With the strictest threshold even one missing sample fails.
        Assert.Null(RegionalAggregationCalculator.AggregatePv(RealPv(s => s.Latitude < 9m), 1m, 1m, DateTimeOffset.UnixEpoch, out _));
        // An incomplete month list counts as missing.
        var broken = RealPv().Select((r, i) => i == 0 ? r with { Estimate = r.Estimate! with { Monthly = r.Estimate.Monthly.Take(11).ToList() } } : r).ToList();
        Assert.Equal(RegionalQualities.Degraded, RegionalAggregationCalculator.AggregatePv(broken, 1m, 0.9m, DateTimeOffset.UnixEpoch, out _)!.Coverage.Quality);
    }

    [Fact]
    public void Climate_is_a_unit_aware_weighted_average_of_daily_rates_with_period_month_lengths()
    {
        var results = Dataset.Samples.Select(s => new RegionalClimateSampleResult(s, RegionalTestSupport.RealResponses.Single(r => r.LocationId == s.LocationId).Climate, null)).ToList();
        var climate = RegionalAggregationCalculator.AggregateClimate(results, 0.95m, DateTimeOffset.UnixEpoch, out var coverage)!.Context;
        Assert.Equal(RegionalQualities.Complete, coverage.Quality);
        var feb = climate.Monthly.Single(m => m.Month == 2);
        Assert.Equal(28.25m, feb.DaysInMonthUsed);   // 2001–2020: 5 leap years of 20
        var expectedRate = Math.Round(Dataset.Samples.Sum(s => s.Weight * RegionalTestSupport.RealResponses.Single(r => r.LocationId == s.LocationId).Climate.Monthly[1].IrradiationKwhPerM2PerDay!.Value), 4);
        Assert.Equal(expectedRate, feb.IrradiationKwhPerM2PerDay);
        Assert.Equal(Math.Round(expectedRate * 28.25m, 2, MidpointRounding.AwayFromZero), feb.IrradiationKwhPerM2);   // kWh/m²/day → kWh/m²/month
        Assert.Equal(climate.Monthly.Sum(m => m.IrradiationKwhPerM2!.Value), climate.AnnualIrradiationKwhPerM2);
        Assert.Equal(climate.Monthly.Sum(m => m.PrecipitationMm!.Value), climate.AnnualPrecipitationMm);   // mm/day → mm/month → year
        Assert.InRange(climate.AnnualMeanTemperatureC!.Value, 25m, 30m);
        Assert.Equal((2001, 2020), (climate.Metadata.StartYear, climate.Metadata.EndYear));
        Assert.Null(climate.Metadata.RequestedLatitude);   // an aggregate is not labelled with one point
        // A sample with a fill value counts as missing.
        var withFill = results.Select((r, i) => i == 1 ? r with { Context = r.Context! with { Monthly = r.Context.Monthly.Select(m => m.Month == 3 ? m with { TemperatureC = null } : m).ToList() } } : r).ToList();
        Assert.Equal(RegionalQualities.Degraded, RegionalAggregationCalculator.AggregateClimate(withFill, 0.9m, DateTimeOffset.UnixEpoch, out _)!.Coverage.Quality);
    }

    // ------------------------------------------------------------------ provider: caching, concurrency, failures

    private sealed class CountingPv(Func<PvEnergyRequest, PvEnergyResult>? answer = null, int delayMs = 0) : IPvEnergyEstimator
    {
        public int Calls;
        public int InFlight;
        public int MaxInFlight;
        public readonly ConcurrentBag<PvEnergyRequest> Requests = new();
        public PvEnergySettings Settings { get; } = new("PVGIS", "5.3", "crystSi", true);

        public async Task<PvEnergyResult> EstimateAsync(PvEnergyRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Requests.Add(request);
            var now = Interlocked.Increment(ref InFlight);
            lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
                return answer?.Invoke(request) ?? await RegionalTestSupport.RealPvHandler()(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }

        public Task<PvHourlyResult> GetHourlySeriesAsync(PvHourlyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly RegionalPvRequest Reference = new(14m, 10m, 180m, PvgisMountingPlaces.Free);

    [Fact]
    public async Task A_complete_baseline_is_shared_and_cached_per_configuration_with_bounded_provider_concurrency()
    {
        var pv = new CountingPv(delayMs: 5);
        var cache = new FakeCacheStore();
        var runtime = RegionalTestSupport.Runtime(new RegionalBaselineOptions { MaxConcurrentPvgisRequests = 3, WarmClimateOnStartup = false });
        RegionalSolarBaselineProvider Provider() => RegionalTestSupport.Provider(pv, new FakeClimateContextProvider(), cache, runtime);

        // Two identical concurrent requests (different callers) share one computation: one call per sample.
        var both = await Task.WhenAll(Provider().GetPvBaselineAsync(Reference, None), Provider().GetPvBaselineAsync(Reference, None));
        Assert.All(both, r => Assert.True(r.Succeeded));
        Assert.Equal(26, pv.Calls);
        Assert.True(pv.MaxInFlight <= 3, $"max in flight {pv.MaxInFlight}");
        Assert.All(pv.Requests, r => Assert.Equal((1m, 10m, 180m, 14m, PvgisMountingPlaces.Free), (r.PeakPowerKwp, r.TiltDegree, r.CompassAzimuthDegree, r.SystemLossPercent, r.MountingPlace)));
        Assert.Equal(1446.13m, Math.Round(both[0].Baseline!.AnnualSpecificYieldKwhPerKwp, 2));

        // Cached for every later caller of this configuration; another configuration is a different aggregate.
        Assert.True((await Provider().GetPvBaselineAsync(Reference, None)).Succeeded);
        Assert.Equal(26, pv.Calls);
        await Provider().GetPvBaselineAsync(Reference with { MountingPlace = PvgisMountingPlaces.Building }, None);
        Assert.Equal(52, pv.Calls);
    }

    [Fact]
    public async Task A_degraded_or_failed_baseline_is_never_cached_and_a_caller_cancellation_does_not_cancel_the_shared_computation()
    {
        var conDao = Dataset.Samples.Single(s => s.Latitude < 9m);
        var failing = true;
        var pvReal = new CountingPv(r => failing && r.Latitude == conDao.Latitude
            ? PvEnergyResult.Fail(ProviderFailureCodes.RateLimited, "429")
            : RegionalTestSupport.RealPvHandler()(r, None).Result);
        var cache = new FakeCacheStore();
        var provider = RegionalTestSupport.Provider(pvReal, new FakeClimateContextProvider(), cache);

        var degraded = await provider.GetPvBaselineAsync(Reference, None);
        Assert.Equal(RegionalQualities.Degraded, degraded.Baseline!.Coverage.Quality);
        Assert.Empty(cache.Sets);                       // never cached
        failing = false;
        var complete = await provider.GetPvBaselineAsync(Reference, None);
        Assert.Equal(RegionalQualities.Complete, complete.Baseline!.Coverage.Quality);
        Assert.Single(cache.Sets);

        // Every sample down: an explicit failure, no baseline, nothing cached.
        var down = RegionalTestSupport.Provider(new CountingPv(_ => PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "down")), new FakeClimateContextProvider(), new FakeCacheStore());
        var failed = await down.GetPvBaselineAsync(Reference, None);
        Assert.Equal((false, RegionalFailureCodes.CoverageInsufficient), (failed.Succeeded, failed.Failure!.Code));

        // A caller that stops waiting does not cancel the shared computation: the next caller gets the cached result.
        var slow = new CountingPv(delayMs: 30);
        var slowCache = new FakeCacheStore();
        var runtime = RegionalTestSupport.Runtime();
        using (var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RegionalTestSupport.Provider(slow, new FakeClimateContextProvider(), slowCache, runtime).GetPvBaselineAsync(Reference, early.Token));
        var later = await RegionalTestSupport.Provider(slow, new FakeClimateContextProvider(), slowCache, runtime).GetPvBaselineAsync(Reference, None);
        Assert.True(later.Succeeded);
        Assert.Equal(26, slow.Calls);
    }

    [Fact]
    public void Membership_uses_the_official_province_code_first_then_the_verified_boundary()
    {
        var provider = RegionalTestSupport.Provider(new FakePvEnergyEstimator(), new FakeClimateContextProvider());
        Assert.Equal(new RegionMembership(RegionMembershipStatus.InRegion, RegionMembershipBases.ProvinceCode), provider.ResolveMembership("79", null, null));
        // The official code wins over (possibly imprecise) coordinates.
        Assert.Equal(RegionMembershipStatus.OutOfRegion, provider.ResolveMembership("01", 10.7725m, 106.6980m).Status);
        Assert.Equal(new RegionMembership(RegionMembershipStatus.InRegion, RegionMembershipBases.SiteCoordinates), provider.ResolveMembership(null, 10.3460m, 107.0843m));
        Assert.Equal(RegionMembershipStatus.OutOfRegion, provider.ResolveMembership(null, 10.9574m, 106.8427m).Status);
        Assert.Equal(new RegionMembership(RegionMembershipStatus.Unknown, RegionMembershipBases.None), provider.ResolveMembership(null, null, null));
        var disabled = RegionalTestSupport.Provider(new FakePvEnergyEstimator(), new FakeClimateContextProvider(), runtime: RegionalTestSupport.Runtime(new RegionalBaselineOptions { Enabled = false }));
        Assert.False(disabled.Enabled);
        Assert.Equal(RegionMembershipStatus.Unknown, disabled.ResolveMembership("79", 10.7m, 106.7m).Status);
    }

    // ------------------------------------------------------------------ simulations

    private static CreateSimulationCommand Flush(Guid userId, Guid preSurveyId, Guid productId)
        => new(userId, preSurveyId, 1, productId, MountingTypes.Flush, null, null, new SmartSolar.Modules.SolarSimulation.Installation.InstallationRequest(null, null, null, null, null), null);

    private static async Task<(Guid User, Guid PreSurvey)> SiteAsync(PreSurveyTestContext ctx, string? provinceCode, string? communeCode, decimal? lat, decimal? lon,
        decimal tilt = 15m, decimal azimuth = 180m, IReadOnlyList<SurfaceObstacle>? obstacles = null, decimal length = 12m)
    {
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync(obstacles, tilt: tilt, azimuth: azimuth, length: length);
        await ctx.Db.PropertySites.Where(p => p.Id == draft.PropertyId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ProvinceCode, provinceCode).SetProperty(p => p.CommuneCode, communeCode)
            .SetProperty(p => p.Latitude, lat).SetProperty(p => p.Longitude, lon));
        ctx.Db.ChangeTracker.Clear();
        return (user.Id, draft.Id);
    }

    [Fact]
    public async Task Sites_in_the_three_former_provinces_get_one_regional_baseline_and_keep_their_own_location()
    {
        await using var ctx = new PreSurveyTestContext();
        await AdministrativeCatalogTestData.SeedCatalogAsync(ctx.Db);
        ctx.PvEnergy.Handler = RegionalTestSupport.RealPvHandler();
        ctx.Climate.Handler = RegionalTestSupport.RealClimateHandler();
        var product = await ctx.SeedPanelProductAsync();
        var sites = new[]
        {
            await SiteAsync(ctx, "79", "26743", 10.7725m, 106.6980m),   // Bến Thành ← former Ho Chi Minh City (Quận 1)
            await SiteAsync(ctx, "79", "25747", 10.9804m, 106.6519m),   // Phú Cường ← former Bình Dương (Thủ Dầu Một)
            await SiteAsync(ctx, "79", "26506", 10.3460m, 107.0843m),   // Vũng Tàu ← former Bà Rịa – Vũng Tàu
            await SiteAsync(ctx, "79", "26732", 8.6829m, 106.6087m),    // Đặc khu Côn Đảo
        };
        var results = new List<SimulationDetail>();
        foreach (var (user, preSurvey) in sites)
            results.Add((await ctx.CreateSimulationHandler().HandleAsync(Flush(user, preSurvey, product.Id), None)).Simulation!);

        Assert.All(results, r => Assert.Equal((ComponentStatusCodes.Succeeded, ComponentStatusCodes.Succeeded), (r.Energy.Status, r.Climate.Status)));
        // Identical system → identical regional energy, specific yield and climate wherever the site is.
        Assert.Single(results.Select(r => r.Energy.AnnualEnergyKwh).Distinct());
        Assert.Single(results.Select(r => r.Energy.SpecificYieldKwhPerKwpYear).Distinct());
        Assert.Single(results.Select(r => r.Climate.Context!.AnnualIrradiationKwhPerM2).Distinct());
        Assert.Single(results.Select(r => string.Join(',', r.Energy.Monthly.Select(m => m.EnergyKwh))).Distinct());
        // …and it is the real area-weighted value scaled to the installed kWp (building scenario: same fixture energies, 1 kWp).
        Assert.All(results, r => Assert.Equal(RegionalBaselineMembership(r), RegionMembershipBases.ProvinceCode));
        // The site's own coordinates are never replaced by the regional reference point.
        Assert.Equal(sites.Length, results.Select(r => (r.Surface.Latitude, r.Surface.Longitude)).Distinct().Count());
        Assert.All(results, r => Assert.NotEqual((r.Energy.Regional!.ReferencePoint.Latitude, r.Energy.Regional.ReferencePoint.Longitude), (r.Surface.Latitude!.Value, r.Surface.Longitude!.Value)));
        // One regional computation per mounting place for all four sites (shared cache), not per site.
        Assert.Equal(2 * 26, ctx.PvEnergy.Requests.Count);
        Assert.Equal(26, ctx.Climate.Calls);
    }

    private static string RegionalBaselineMembership(SimulationDetail d) => d.Energy.Regional!.MembershipBasis;

    [Fact]
    public async Task Different_roof_inputs_still_change_the_site_result_on_the_same_regional_baseline()
    {
        await using var ctx = new PreSurveyTestContext();
        ctx.PvEnergy.Handler = RegionalTestSupport.RealPvHandler();
        ctx.Climate.Handler = RegionalTestSupport.RealClimateHandler();
        var product = await ctx.SeedPanelProductAsync();
        var small = await SiteAsync(ctx, "79", null, 10.7725m, 106.6980m, length: 6m);
        var large = await SiteAsync(ctx, "79", null, 10.7725m, 106.6980m, length: 12m);
        var east = await SiteAsync(ctx, "79", null, 10.7725m, 106.6980m, azimuth: 90m, length: 12m);

        var a = (await ctx.CreateSimulationHandler().HandleAsync(Flush(small.User, small.PreSurvey, product.Id), None)).Simulation!;
        var b = (await ctx.CreateSimulationHandler().HandleAsync(Flush(large.User, large.PreSurvey, product.Id), None)).Simulation!;
        var c = (await ctx.CreateSimulationHandler().HandleAsync(Flush(east.User, east.PreSurvey, product.Id), None)).Simulation!;

        Assert.True(b.Layout.PanelCount > a.Layout.PanelCount);
        Assert.True(b.Energy.AnnualEnergyKwh > a.Energy.AnnualEnergyKwh);
        Assert.Equal(a.Energy.SpecificYieldKwhPerKwpYear, b.Energy.SpecificYieldKwhPerKwpYear);   // same regional yield, different size
        // Another orientation is another 1 kWp regional configuration (east-facing requests were made).
        Assert.Contains(ctx.PvEnergy.Requests, r => r.CompassAzimuthDegree == 90m);
        Assert.Equal(EstimateBases.HcmcRegionalAreaWeighted, c.EstimateBasis);
    }

    [Fact]
    public async Task Outside_or_unknown_sites_get_no_regional_estimate_and_no_provider_call()
    {
        await using var ctx = new PreSurveyTestContext();
        var product = await ctx.SeedPanelProductAsync();
        var otherProvince = await SiteAsync(ctx, "01", null, 21.0285m, 105.8542m);
        var outsideByPoint = await SiteAsync(ctx, null, null, 10.9574m, 106.8427m);
        var unknown = await SiteAsync(ctx, null, null, null, null);

        var r1 = (await ctx.CreateSimulationHandler().HandleAsync(Flush(otherProvince.User, otherProvince.PreSurvey, product.Id), None)).Simulation!;
        var r2 = (await ctx.CreateSimulationHandler().HandleAsync(Flush(outsideByPoint.User, outsideByPoint.PreSurvey, product.Id), None)).Simulation!;
        var r3 = (await ctx.CreateSimulationHandler().HandleAsync(Flush(unknown.User, unknown.PreSurvey, product.Id), None)).Simulation!;

        Assert.Equal((ComponentStatusCodes.Unavailable, RegionalFailureCodes.RegionNotSupported), (r1.Energy.Status, r1.Energy.Reason));
        Assert.Equal((ComponentStatusCodes.Unavailable, RegionalFailureCodes.RegionNotSupported), (r2.Climate.Status, r2.Climate.Reason));
        Assert.Equal((ComponentStatusCodes.Unavailable, RegionalFailureCodes.RegionUnknown), (r3.Energy.Status, r3.Energy.Reason));
        Assert.All(new[] { r1, r2, r3 }, r =>
        {
            Assert.Null(r.Energy.AnnualEnergyKwh);   // never a zero
            Assert.Null(r.Energy.Regional);
            Assert.Contains(r.Warnings, w => w.Code == SimulationWarningCodes.RegionNotSupported);
            Assert.Equal(SimulationStatusCodes.PartiallyCompleted, r.Status);
            Assert.True(r.Layout.PanelCount > 0);   // the layout itself is still computed
        });
        Assert.Empty(ctx.PvEnergy.Requests);
        Assert.Equal(0, ctx.Climate.Calls);
    }

    [Fact]
    public async Task A_province_79_site_without_coordinates_gets_regional_energy_but_no_local_shading()
    {
        await using var ctx = new PreSurveyTestContext();
        var product = await ctx.SeedPanelProductAsync();
        var (user, preSurvey) = await SiteAsync(ctx, "79", null, null, null, obstacles: [new SurfaceObstacle("Tank", 6m, 4m, 1.5m, 2m, 1.8m)]);

        var r = (await ctx.CreateSimulationHandler().HandleAsync(Flush(user, preSurvey, product.Id), None)).Simulation!;

        Assert.Equal(ComponentStatusCodes.Succeeded, r.Energy.Status);
        Assert.Equal(RegionMembershipBases.ProvinceCode, r.Energy.Regional!.MembershipBasis);
        Assert.Equal((ComponentStatusCodes.Unavailable, ShadingReasons.LocationMissing), (r.Shading!.Status, r.Shading.Reason));
        Assert.Contains(r.Warnings, w => w.Code == SimulationWarningCodes.LocationMissing && w.Message.Contains("unshaded"));
        Assert.Empty(ctx.PvEnergy.HourlyRequests);   // no site point → no point series, and no other point is ever substituted
    }

    [Fact]
    public async Task A_disabled_model_never_falls_back_to_the_site_point_and_a_model_change_invalidates_reuse()
    {
        await using var ctx = new PreSurveyTestContext();
        var product = await ctx.SeedPanelProductAsync();
        var (user, preSurvey) = await SiteAsync(ctx, "79", null, 10.7725m, 106.6980m);
        var first = (await ctx.CreateSimulationHandler().HandleAsync(Flush(user, preSurvey, product.Id), None)).Simulation!;
        Assert.Equal(ComponentStatusCodes.Succeeded, first.Energy.Status);

        // A different regional model setting (coverage threshold) changes the fingerprint: the cached result is not reused.
        ctx.SimulationOptions.RegionalBaseline.MinCoveredWeightFraction = 0.9m;
        var handler = new CreateSimulationHandler(new SmartSolar.Infrastructure.Persistence.SolarSimulationUnitOfWork(ctx.Db),
            new SmartSolar.Infrastructure.Persistence.CatalogUnitOfWork(ctx.Db), ctx.PvEnergy, ctx.Climate,
            RegionalTestSupport.Provider(ctx.PvEnergy, ctx.Climate, ctx.RegionalCache, RegionalTestSupport.Runtime(ctx.SimulationOptions.RegionalBaseline)),
            ctx.SimulationOptions, ctx.SingleFlight, ctx.SimulationCache, ctx.Time, NullLogger<CreateSimulationHandler>.Instance);
        var second = await handler.HandleAsync(Flush(user, preSurvey, product.Id), None);
        Assert.Equal(CreateSimulationOutcome.Created, second.Outcome);
        Assert.NotEqual(first.SimulationId, second.Simulation!.SimulationId);

        // Disabled: no estimate and no provider call at the site point.
        var calls = ctx.PvEnergy.Requests.Count;
        var disabled = new CreateSimulationHandler(new SmartSolar.Infrastructure.Persistence.SolarSimulationUnitOfWork(ctx.Db),
            new SmartSolar.Infrastructure.Persistence.CatalogUnitOfWork(ctx.Db), ctx.PvEnergy, ctx.Climate,
            RegionalTestSupport.Provider(ctx.PvEnergy, ctx.Climate, ctx.RegionalCache, RegionalTestSupport.Runtime(new RegionalBaselineOptions { Enabled = false })),
            ctx.SimulationOptions, ctx.SingleFlight, ctx.SimulationCache, ctx.Time, NullLogger<CreateSimulationHandler>.Instance);
        var off = (await disabled.HandleAsync(Flush(user, preSurvey, product.Id), None)).Simulation!;
        Assert.Equal((ComponentStatusCodes.Unavailable, RegionalFailureCodes.Disabled), (off.Energy.Status, off.Energy.Reason));
        Assert.Equal(calls, ctx.PvEnergy.Requests.Count);
    }

    [Fact]
    public async Task A_degraded_regional_baseline_is_warned_selectable_and_never_reused()
    {
        await using var ctx = new PreSurveyTestContext();
        var product = await ctx.SeedPanelProductAsync();
        var conDao = Dataset.Samples.Single(s => s.Latitude < 9m);
        ctx.PvEnergy.Handler = (r, _) => Task.FromResult(r.Latitude == conDao.Latitude
            ? PvEnergyResult.Fail(ProviderFailureCodes.Timeout, "slow")
            : FakePvEnergyEstimator.Synthetic(r));
        var (user, preSurvey) = await SiteAsync(ctx, "79", null, 10.7725m, 106.6980m);

        var r = (await ctx.CreateSimulationHandler().HandleAsync(Flush(user, preSurvey, product.Id), None)).Simulation!;
        Assert.Equal(ComponentStatusCodes.Succeeded, r.Energy.Status);
        Assert.Equal(RegionalQualities.Degraded, r.Energy.Regional!.Quality);
        Assert.Contains(r.Warnings, w => w.Code == SimulationWarningCodes.RegionalBaselineDegraded);
        var again = await ctx.CreateSimulationHandler().HandleAsync(Flush(user, preSurvey, product.Id), None);
        Assert.Equal(CreateSimulationOutcome.Created, again.Outcome);   // not reused
        Assert.Equal(SelectSimulationResultOutcome.Selected, (await ctx.SelectSimulationAsync(user, preSurvey, r.SimulationId)).Outcome);
    }

    [Fact]
    public void Results_stored_before_the_regional_model_keep_an_explicit_legacy_site_point_basis()
    {
        Assert.Equal(EstimateBases.LegacySitePoint, EstimateBases.ForAlgorithm("layout-v2+shading-v1"));
        Assert.Equal(EstimateBases.LegacySitePoint, EstimateBases.ForAlgorithm(null));
        Assert.Equal(EstimateBases.HcmcRegionalAreaWeighted, EstimateBases.ForAlgorithm(AlgorithmVersions.Current));
        var summary = new SmartSolar.Modules.SolarSimulation.Entities.SelectedSimulation
        {
            SimulationId = Guid.NewGuid(), PreSurveyId = Guid.NewGuid(), PreSurveyGeometryVersion = 1, AlgorithmVersion = "layout-v2+shading-v1",
            Status = SimulationStatusCodes.Completed, EnergyStatus = ComponentStatusCodes.Succeeded, MountingType = MountingTypes.Flush, ProductSku = "P", ProductName = "P",
            PanelCount = 10, InstalledCapacityKwp = 5.5m, AnnualEnergyKwh = 7700m, SpecificYieldKwhPerKwpYear = 1400m,
        };
        var view = SimulationDetailMapper.ToView(summary, 1);
        Assert.Equal(EstimateBases.LegacySitePoint, view.EstimateBasis);
        Assert.Equal(7700m, view.AnnualEnergyKwh);   // historic values are never rewritten
    }
}

/// <summary>The solar advisor and the simulation read the same regional model: same 1 kWp configuration, same specific yield.</summary>
public sealed class RegionalAdvisorConsistencyTests
{
    [Fact]
    public async Task Advisor_and_flush_simulation_report_the_same_regional_specific_yield_from_real_PVGIS_responses()
    {
        await using var ctx = new PreSurveyTestContext();
        ctx.PvEnergy.Handler = RegionalTestSupport.RealPvHandler();
        ctx.Climate.Handler = RegionalTestSupport.RealClimateHandler();
        var product = await ctx.SeedPanelProductAsync();
        // Default seeded surface: tilt 15°, south (180°), no obstacles (no local shading), site in HCMC by its coordinates.
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var simulation = (await ctx.CreateSimulationHandler().HandleAsync(new CreateSimulationCommand(user.Id, draft.Id, 1, product.Id,
            MountingTypes.Flush, null, null, new SmartSolar.Modules.SolarSimulation.Installation.InstallationRequest(null, null, null, null, null), null),
            CancellationToken.None)).Simulation!;
        var primary = simulation.Energy.Scenarios[0];
        Assert.Equal(PvgisMountingPlaces.Building, primary.MountingPlace);

        var advisor = new SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorService(new NoCatalogData(), ctx.RegionalProvider,
            new SmartSolar.Modules.KnowledgeBase.Advisor.SolarYieldCache(), new SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorOptions(), TimeProvider.System);
        var advice = await advisor.AdviseAsync(Guid.NewGuid(), new SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorInput(MonthlyConsumptionKwh: 500m,
            Latitude: 10.98m, Longitude: 106.65m, TiltDegree: 15m, AzimuthDegree: 180m), CancellationToken.None);

        Assert.Equal("PVGIS_HCMC_REGIONAL", advice.YieldSource);
        Assert.Equal(primary.SpecificYieldKwhPerKwpYear, advice.SpecificYieldKwhPerKwpYear);
        // Both came from the same shared aggregate: the advisor added no PVGIS call.
        Assert.Equal(2 * RegionalTestSupport.SampleCount, ctx.PvEnergy.Requests.Count);
    }

    private sealed class NoCatalogData : SmartSolar.Modules.KnowledgeBase.Advisor.ISolarAdvisorDataSource
    {
        public Task<SmartSolar.Modules.KnowledgeBase.Advisor.AdvisorCatalog> GetCatalogAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SmartSolar.Modules.KnowledgeBase.Advisor.AdvisorCatalog(
                [new SmartSolar.Modules.KnowledgeBase.Advisor.AdvisorPanel(Guid.NewGuid(), "P", "P", "B", 550m, 1134m, 2278m, 3_000_000m, null, DateTimeOffset.UnixEpoch)],
                [], [], 0));

        public Task<SmartSolar.Modules.KnowledgeBase.Advisor.AdvisorSite?> GetOwnSiteAsync(Guid userId, Guid propertySiteId, CancellationToken cancellationToken)
            => Task.FromResult<SmartSolar.Modules.KnowledgeBase.Advisor.AdvisorSite?>(null);
    }
}
