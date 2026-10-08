using System.Net;
using System.Web;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.ExternalProviders;

/// <summary>
/// SYNTHETIC fixtures that copy the NASA POWER climatology JSON structure (header,
/// parameters, properties.parameter). Values are invented for unit-conversion tests.
/// </summary>
public sealed class NasaPowerClimatologyClientTests
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    internal static string SyntheticResponse(
        string precipitationUnits = "mm/day",
        bool includeTemperature = true,
        double febPrecipitation = 0.2)
    {
        string Series(Func<int, double> value, double annual)
            => "{" + string.Join(",", Months.Select((m, i) => $"\"{m}\":{value(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + $",\"ANN\":{annual.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + "}";

        var temperature = includeTemperature ? ",\"T2M\":" + Series(_ => 27.0, 27.5) : "";
        var temperatureUnits = includeTemperature ? ",\"T2M\":{\"units\":\"C\",\"longname\":\"Temperature at 2 Meters\"}" : "";

        return "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[106.7,10.78,7.0]},"
            + "\"properties\":{\"parameter\":{"
            + "\"ALLSKY_SFC_SW_DWN\":" + Series(_ => 5.0, 5.0) + ","
            + "\"PRECTOTCORR\":" + Series(m => m == 2 ? febPrecipitation : 1.0, 1.0) + temperature + "}},"
            + "\"header\":{\"title\":\"NASA/POWER Source Native Resolution Climatology Climatologies\",\"api\":{\"version\":\"v2.10.0\",\"name\":\"POWER Climatology API\"},"
            + "\"sources\":[\"SYN1DEG\",\"MERRA2\"],\"fill_value\":-999.0,\"time_standard\":\"LST\","
            + "\"range\":\"20-year Meteorological and Solar Monthly & Annual Climatologies (January 2001 - December 2020)\"},"
            + "\"messages\":[],"
            + "\"parameters\":{\"ALLSKY_SFC_SW_DWN\":{\"units\":\"kW-hr/m^2/day\",\"longname\":\"All Sky Surface Shortwave Downward Irradiance\"},"
            + "\"PRECTOTCORR\":{\"units\":\"" + precipitationUnits + "\",\"longname\":\"Precipitation Corrected\"}" + temperatureUnits + "},"
            + "\"times\":{\"data\":1.0,\"process\":1.0}}";
    }

    [Theory]
    [InlineData(1, 2001, 2020, 31.0)]
    [InlineData(2, 2001, 2020, 28.25)]
    [InlineData(2, 2004, 2004, 29.0)]
    [InlineData(4, 2001, 2020, 30.0)]
    public void Mean_month_length_counts_leap_days_proportionally(int month, int start, int end, double expected)
        => Assert.Equal((decimal)expected, ClimatologyConversion.MeanDaysInMonth(month, start, end));

    [Fact]
    public async Task Converts_daily_rates_to_monthly_totals_and_keeps_both()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse());
        var client = ProviderTestHost.NasaPower(stub, new FakeCacheStore());

        var result = await client.GetMonthlyClimatologyAsync(10.7769m, 106.7008m, CancellationToken.None);

        Assert.True(result.Succeeded);
        var context = result.Context!;
        var january = context.Monthly[0];
        var february = context.Monthly[1];
        Assert.Equal(31m, january.DaysInMonthUsed);
        Assert.Equal(1.0m, january.PrecipitationMmPerDay);
        Assert.Equal(31.00m, january.PrecipitationMm);
        Assert.Equal(155.00m, january.IrradiationKwhPerM2);
        Assert.Equal(28.25m, february.DaysInMonthUsed);
        Assert.Equal(5.65m, february.PrecipitationMm);  // 0.2 mm/day x 28.25 days
        Assert.Equal(27.0m, january.TemperatureC);
        // 11 months x ~1.0 mm/day x their days + February 5.65 mm.
        Assert.Equal(365.25m - 28.25m + 5.65m, context.AnnualPrecipitationMm);
        Assert.Equal(27.5m, context.AnnualMeanTemperatureC);
        Assert.Equal("v2.10.0", context.Metadata.ApiVersion);
        Assert.Equal(2001, context.Metadata.StartYear);
        Assert.Equal(2020, context.Metadata.EndYear);
        Assert.Equal("mm/day", context.Metadata.ParameterUnits["PRECTOTCORR"]);
    }

    [Fact]
    public async Task Requests_rounded_coordinates_period_and_parameters()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse());

        await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.7769m, 106.7008m, CancellationToken.None);

        var uri = Assert.Single(stub.Requests).RequestUri!;
        Assert.Equal("https://power.larc.nasa.gov/api/temporal/climatology/point", uri.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("10.78", query["latitude"]);
        Assert.Equal("106.70", query["longitude"]);
        Assert.Equal("2001", query["start"]);
        Assert.Equal("2020", query["end"]);
        Assert.Equal("RE", query["community"]);
        Assert.Equal("JSON", query["format"]);
        Assert.Equal("ALLSKY_SFC_SW_DWN,T2M,PRECTOTCORR", query["parameters"]);
    }

    [Fact]
    public async Task Fill_values_become_missing_and_annual_totals_are_not_invented()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse(febPrecipitation: -999.0));

        var result = await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.78m, 106.70m, CancellationToken.None);

        Assert.Null(result.Context!.Monthly[1].PrecipitationMmPerDay);
        Assert.Null(result.Context.Monthly[1].PrecipitationMm);
        Assert.Null(result.Context.AnnualPrecipitationMm);
    }

    [Fact]
    public async Task Unexpected_units_are_rejected_instead_of_misconverted()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse(precipitationUnits: "mm"));

        var result = await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.78m, 106.70m, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.ResponseInvalid, result.Failure!.Code);
    }

    [Fact]
    public async Task Missing_parameter_is_reported_as_invalid()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, SyntheticResponse(includeTemperature: false));

        var result = await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.78m, 106.70m, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.ResponseInvalid, result.Failure!.Code);
    }

    [Fact]
    public async Task Service_unavailable_is_retried_then_reported()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.ServiceUnavailable, "{}");

        var result = await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.78m, 106.70m, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.Unavailable, result.Failure!.Code);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Hanging_provider_times_out_as_a_failure()
    {
        var stub = new StubHttpMessageHandler().AlwaysHang();

        var result = await ProviderTestHost.NasaPower(stub, new FakeCacheStore()).GetMonthlyClimatologyAsync(10.78m, 106.70m, CancellationToken.None);

        Assert.Equal(ProviderFailureCodes.Timeout, result.Failure!.Code);
    }

    [Fact]
    public async Task Nearby_coordinates_share_one_cached_response()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.OK, SyntheticResponse());
        var client = ProviderTestHost.NasaPower(stub, new FakeCacheStore());

        await client.GetMonthlyClimatologyAsync(10.7769m, 106.7008m, CancellationToken.None);
        var second = await client.GetMonthlyClimatologyAsync(10.7771m, 106.7012m, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Single(stub.Requests);
    }
}
