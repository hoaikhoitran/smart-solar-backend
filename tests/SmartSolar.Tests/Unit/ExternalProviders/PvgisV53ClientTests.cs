using System.Net;
using System.Web;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.ExternalProviders;

/// <summary>
/// All responses here are SYNTHETIC fixtures that copy the PVGIS 5.3 PVcalc JSON structure
/// (inputs.meteo_data, outputs.monthly.fixed, outputs.totals.fixed). The numbers are invented
/// round values for parsing tests and are not real irradiation or energy data.
/// </summary>
public sealed class PvgisV53ClientTests
{
    private static readonly PvEnergyRequest Request = new(
        Latitude: 10.7769m, Longitude: 106.7008m, PeakPowerKwp: 26.4m, SystemLossPercent: 14m,
        TiltDegree: 12m, CompassAzimuthDegree: 90m, MountingPlace: PvgisMountingPlaces.Building);

    internal static string SyntheticResponse(int months = 12, bool includeTotals = true, string lSpec = "\"1.0\"")
    {
        var monthly = string.Join(",", Enumerable.Range(1, months).Select(m =>
            "{\"month\":" + m + ",\"E_d\":10.0,\"E_m\":" + (300 + m) + ".0,\"H(i)_d\":5.0,\"H(i)_m\":150.0,\"SD_m\":10.0}"));
        var totals = includeTotals
            ? ",\"totals\":{\"fixed\":{\"E_d\":10.0,\"E_m\":306.5,\"E_y\":3678.0,\"H(i)_d\":5.0,\"H(i)_m\":150.0,\"H(i)_y\":1800.0,\"SD_m\":5.0,\"SD_y\":60.0,\"l_aoi\":-3.0,\"l_spec\":" + lSpec + ",\"l_tg\":-10.0,\"l_total\":-25.0}}"
            : "";
        return "{\"inputs\":{\"location\":{\"latitude\":10.7769,\"longitude\":106.7008,\"elevation\":12.0},"
            + "\"meteo_data\":{\"radiation_db\":\"PVGIS-ERA5\",\"meteo_db\":\"ERA5\",\"year_min\":2005,\"year_max\":2023,\"use_horizon\":true,\"horizon_db\":\"DEM-calculated\"},"
            + "\"mounting_system\":{\"fixed\":{\"slope\":{\"value\":12,\"optimal\":false},\"azimuth\":{\"value\":-90,\"optimal\":false},\"type\":\"building-integrated\"}},"
            + "\"pv_module\":{\"technology\":\"c-Si\",\"peak_power\":26.4,\"system_loss\":14.0}},"
            + "\"outputs\":{\"monthly\":{\"fixed\":[" + monthly + "]}" + totals + "},"
            + "\"meta\":{}}";
    }

    [Fact]
    public async Task Parses_monthly_and_annual_energy_with_provider_metadata()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse());
        var client = ProviderTestHost.Pvgis(stub, new FakeCacheStore());

        var result = await client.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.Succeeded);
        var estimate = result.Estimate!;
        Assert.Equal(3678.0m, estimate.AnnualEnergyKwh);
        Assert.Equal(60.0m, estimate.AnnualStdDevKwh);
        Assert.Equal(-25.0m, estimate.TotalLossPercent);
        Assert.Equal(12, estimate.Monthly.Count);
        Assert.Equal(301.0m, estimate.Monthly[0].EnergyKwh);
        Assert.Equal(312.0m, estimate.Monthly[11].EnergyKwh);
        Assert.Equal(150.0m, estimate.Monthly[0].InPlaneIrradiationKwhPerM2);
        Assert.Equal("PVGIS-ERA5", estimate.Provider.RadiationDatabase);
        Assert.Equal(2005, estimate.Provider.YearMin);
        Assert.Equal(2023, estimate.Provider.YearMax);
        Assert.Equal(-90m, estimate.Provider.AspectSentDegree);
        Assert.Equal(PvgisMountingPlaces.Building, estimate.Provider.MountingPlace);
    }

    [Fact]
    public async Task Sends_converted_aspect_and_invariant_numbers()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse());
        var client = ProviderTestHost.Pvgis(stub, new FakeCacheStore());

        await client.EstimateAsync(Request, CancellationToken.None);

        var uri = Assert.Single(stub.Requests).RequestUri!;
        Assert.Equal("https://re.jrc.ec.europa.eu/api/v5_3/PVcalc", uri.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("10.7769", query["lat"]);
        Assert.Equal("106.7008", query["lon"]);
        Assert.Equal("26.4", query["peakpower"]);
        Assert.Equal("14", query["loss"]);
        Assert.Equal("12", query["angle"]);
        Assert.Equal("-90", query["aspect"]);
        Assert.Equal("building", query["mountingplace"]);
        Assert.Equal("crystSi", query["pvtechchoice"]);
        Assert.Equal("1", query["usehorizon"]);
        Assert.Equal("json", query["outputformat"]);
    }

    [Fact]
    public async Task Tolerates_numeric_strings_in_loss_fields()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse(lSpec: "\"0.81\""));
        var client = ProviderTestHost.Pvgis(stub, new FakeCacheStore());

        Assert.True((await client.EstimateAsync(Request, CancellationToken.None)).Succeeded);
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public async Task Incomplete_responses_are_reported_as_invalid(int months, bool totals)
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse(months, totals));
        var client = ProviderTestHost.Pvgis(stub, new FakeCacheStore());

        var result = await client.EstimateAsync(Request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureCodes.ResponseInvalid, result.Failure!.Code);
    }

    [Fact]
    public async Task Malformed_json_is_reported_as_invalid()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{ not json");
        var result = await ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.ResponseInvalid, result.Failure!.Code);
    }

    [Fact]
    public async Task Out_of_coverage_is_reported_without_retrying()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.BadRequest,
            """{"message":"Location out of the spatial coverage of the radiation database selected. Please, selectanother database (PVGIS-ERA5).","status":400}""");

        var result = await ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.LocationNotCovered, result.Failure!.Code);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Rate_limited_request_is_retried_then_succeeds()
    {
        var stub = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")
            .Enqueue(HttpStatusCode.OK, SyntheticResponse());

        var result = await ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Server_errors_are_retried_twice_then_reported()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.InternalServerError, "{}");

        var result = await ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.Unavailable, result.Failure!.Code);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Hanging_provider_times_out_as_a_failure()
    {
        var stub = new StubHttpMessageHandler().AlwaysHang();

        var result = await ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.Timeout, result.Failure!.Code);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var stub = new StubHttpMessageHandler().AlwaysHang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderTestHost.Pvgis(stub, new FakeCacheStore()).EstimateAsync(Request, cts.Token));
    }

    [Fact]
    public async Task Identical_requests_are_served_from_cache_and_parameters_are_part_of_the_key()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.OK, SyntheticResponse());
        var cache = new FakeCacheStore();
        var client = ProviderTestHost.Pvgis(stub, cache);

        await client.EstimateAsync(Request, CancellationToken.None);
        var cached = await client.EstimateAsync(Request, CancellationToken.None);
        await client.EstimateAsync(Request with { MountingPlace = PvgisMountingPlaces.Free }, CancellationToken.None);

        Assert.True(cached.Succeeded);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var stub = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{ broken")
            .Enqueue(HttpStatusCode.OK, SyntheticResponse());
        var client = ProviderTestHost.Pvgis(stub, new FakeCacheStore());

        Assert.False((await client.EstimateAsync(Request, CancellationToken.None)).Succeeded);
        Assert.True((await client.EstimateAsync(Request, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Cache_outage_falls_back_to_the_provider()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse());
        var cache = new FakeCacheStore { Fail = true };

        Assert.True((await ProviderTestHost.Pvgis(stub, cache).EstimateAsync(Request, CancellationToken.None)).Succeeded);
    }
}
