using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Regional;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The shipped 2025 HCMC regional dataset (copied next to the test assembly by the API project) and helpers to build the regional
/// baseline provider over the test fakes, plus the REAL provider responses recorded for its 26 samples (TestData/RegionalBaseline).
/// </summary>
public static class RegionalTestSupport
{
    public const string DatasetKey = "hcm-79-2025-equal-area-20km-v1";

    public static string DatasetDirectory => Path.Combine(AppContext.BaseDirectory, "ReferenceData", "RegionalBaseline", DatasetKey);

    private static readonly Lazy<RegionalDataset> Shared = new(() => RegionalDataset.Load(DatasetDirectory));

    /// <summary>The validated shipped dataset (loaded once).</summary>
    public static RegionalDataset Dataset => Shared.Value;

    /// <summary>Provider calls of one regional aggregate (one per sample).</summary>
    public static int SampleCount => Dataset.Samples.Count;

    /// <summary>
    /// Site annual energy from the SYNTHETIC fake (<see cref="FakePvEnergyEstimator.Synthetic"/>: monthly = round(annual/12, 2) at 1 kWp, the same at
    /// every sample) scaled as the API does: Σ_m round(Y[m] × kWp, 2).
    /// </summary>
    public static decimal SyntheticSiteAnnual(decimal annualPerKwp, decimal kwp)
        => 12 * Math.Round(Math.Round(annualPerKwp / 12m, 2) * kwp, 2, MidpointRounding.AwayFromZero);

    public static RegionalBaselineRuntime Runtime(RegionalBaselineOptions? options = null)
    {
        var o = options ?? new RegionalBaselineOptions { WarmClimateOnStartup = false };
        return new RegionalBaselineRuntime(o, o.Enabled ? Dataset : null);
    }

    public static RegionalSolarBaselineProvider Provider(IPvEnergyEstimator pv, IClimateContextProvider climate, ICacheStore? cache = null,
        RegionalBaselineRuntime? runtime = null, TimeProvider? time = null)
        => new(runtime ?? Runtime(), pv, climate, cache ?? new FakeCacheStore(), time ?? TimeProvider.System, NullLogger<RegionalSolarBaselineProvider>.Instance);

    // ------------------------------------------------------------------ real provider responses (fixture)

    public sealed record FixtureSample(string LocationId, decimal Latitude, decimal Longitude, PvEnergyEstimate Pvgis, ClimateContext Climate);

    private static readonly Lazy<IReadOnlyList<FixtureSample>> Fixture = new(LoadFixture);

    /// <summary>REAL PVGIS 5.3 (1 kWp, 10°, south, 14 %, free, crystSi, horizon) and NASA POWER (2001–2020) responses for the 26 samples.</summary>
    public static IReadOnlyList<FixtureSample> RealResponses => Fixture.Value;

    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "TestData", "RegionalBaseline", "hcm-20km-real-provider-responses.json");

    private static IReadOnlyList<FixtureSample> LoadFixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var keys = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        var list = new List<FixtureSample>();
        foreach (var s in doc.RootElement.GetProperty("samples").EnumerateArray())
        {
            var lat = decimal.Parse(s.GetProperty("latitude").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var lon = decimal.Parse(s.GetProperty("longitude").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var p = s.GetProperty("pvgis");
            var meteo = p.GetProperty("meteo");
            var monthly = p.GetProperty("monthly").EnumerateArray()
                .Select(m => new PvMonthlyEnergy(m.GetProperty("month").GetInt32(), m.GetProperty("E_m").GetDecimal(), m.GetProperty("SD_m").GetDecimal(),
                    m.GetProperty("H(i)_m").GetDecimal())).ToList();
            var totals = p.GetProperty("totals");
            var pv = new PvEnergyEstimate(totals.GetProperty("E_y").GetDecimal(), totals.GetProperty("SD_y").GetDecimal(), totals.GetProperty("H(i)_y").GetDecimal(),
                totals.GetProperty("l_total").GetDecimal(), monthly,
                new PvProviderMetadata("PVGIS", "5.3", meteo.GetProperty("radiation_db").GetString(), meteo.GetProperty("meteo_db").GetString(),
                    meteo.GetProperty("year_min").GetInt32(), meteo.GetProperty("year_max").GetInt32(), meteo.GetProperty("horizon_db").GetString(), "free", "crystSi", true,
                    1m, 14m, 10m, 0m, new DateTimeOffset(2026, 11, 23, 13, 0, 0, TimeSpan.Zero)));

            var n = s.GetProperty("nasa");
            var par = n.GetProperty("parameter");
            var months = new List<ClimateMonthly>();
            for (var i = 0; i < 12; i++)
            {
                var days = ClimatologyConversion.MeanDaysInMonth(i + 1, 2001, 2020);
                var irr = par.GetProperty("ALLSKY_SFC_SW_DWN").GetProperty(keys[i]).GetDecimal();
                var rain = par.GetProperty("PRECTOTCORR").GetProperty(keys[i]).GetDecimal();
                months.Add(new ClimateMonthly(i + 1, days, irr, ClimatologyConversion.MonthlyTotal(irr, days), par.GetProperty("T2M").GetProperty(keys[i]).GetDecimal(),
                    rain, ClimatologyConversion.MonthlyTotal(rain, days)));
            }
            var climate = new ClimateContext(months, months.Sum(m => m.IrradiationKwhPerM2), months.Sum(m => m.PrecipitationMm),
                par.GetProperty("T2M").GetProperty("ANN").GetDecimal(),
                new ClimateMetadata("NASA POWER", n.GetProperty("apiVersion").GetString(), 2001, 2020, "RE",
                    n.GetProperty("sources").EnumerateArray().Select(x => x.GetString()!).ToList(), lat, lon,
                    new Dictionary<string, string> { ["ALLSKY_SFC_SW_DWN"] = "kW-hr/m^2/day", ["T2M"] = "C", ["PRECTOTCORR"] = "mm/day" },
                    new DateTimeOffset(2026, 11, 23, 13, 0, 0, TimeSpan.Zero)));
            list.Add(new FixtureSample(s.GetProperty("locationId").GetString()!, lat, lon, pv, climate));
        }
        return list;
    }

    /// <summary>A PV handler answering every regional sample with its recorded real PVGIS response (scaled by peak power).</summary>
    public static Func<PvEnergyRequest, CancellationToken, Task<PvEnergyResult>> RealPvHandler()
        => (request, _) =>
        {
            var hit = RealResponses.FirstOrDefault(s => s.Latitude == request.Latitude && s.Longitude == request.Longitude);
            if (hit is null) return Task.FromResult(PvEnergyResult.Fail(ProviderFailureCodes.LocationNotCovered, "Not a recorded sample."));
            var k = request.PeakPowerKwp;
            var e = hit.Pvgis;
            return Task.FromResult(PvEnergyResult.Success(e with
            {
                AnnualEnergyKwh = e.AnnualEnergyKwh * k,
                Monthly = e.Monthly.Select(m => m with { EnergyKwh = m.EnergyKwh * k }).ToList(),
                Provider = e.Provider with { PeakPowerKwp = k, MountingPlace = request.MountingPlace },
            }));
        };

    public static Func<decimal, decimal, CancellationToken, Task<ClimateContextResult>> RealClimateHandler()
        => (lat, lon, _) =>
        {
            var hit = RealResponses.FirstOrDefault(s => s.Latitude == lat && s.Longitude == lon);
            return Task.FromResult(hit is null
                ? ClimateContextResult.Fail(ProviderFailureCodes.LocationNotCovered, "Not a recorded sample.")
                : ClimateContextResult.Success(hit.Climate));
        };
}
