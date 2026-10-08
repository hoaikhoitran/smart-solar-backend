using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SYNTHETIC energy provider for tests. Default results are invented round numbers
/// (1400 kWh/kWp for "building", 1450 kWh/kWp for "free"); they are not PVGIS data.
/// </summary>
public sealed class FakePvEnergyEstimator : IPvEnergyEstimator
{
    private readonly object _gate = new();
    private readonly List<PvEnergyRequest> _requests = new();

    public PvEnergySettings Settings { get; } = new("PVGIS", "5.3", "crystSi", true);

    /// <summary>Optional override; default returns a synthetic success.</summary>
    public Func<PvEnergyRequest, CancellationToken, Task<PvEnergyResult>>? Handler { get; set; }

    public IReadOnlyList<PvEnergyRequest> Requests { get { lock (_gate) return _requests.ToList(); } }

    public async Task<PvEnergyResult> EstimateAsync(PvEnergyRequest request, CancellationToken cancellationToken)
    {
        lock (_gate) _requests.Add(request);
        return Handler is null ? Synthetic(request) : await Handler(request, cancellationToken);
    }

    public static PvEnergyResult Synthetic(PvEnergyRequest request)
    {
        var yield = request.MountingPlace == PvgisMountingPlaces.Building ? 1400m : 1450m;
        var annual = Math.Round(yield * request.PeakPowerKwp, 2);
        var monthly = Enumerable.Range(1, 12).Select(m => new PvMonthlyEnergy(m, Math.Round(annual / 12m, 2), 1m, 150m)).ToList();
        return PvEnergyResult.Success(new PvEnergyEstimate(annual, 10m, 1800m, -20m, monthly,
            new PvProviderMetadata("PVGIS", "5.3", "SYNTHETIC-DB", "SYNTHETIC", 2005, 2023, "SYNTHETIC", request.MountingPlace,
                "crystSi", true, request.PeakPowerKwp, request.SystemLossPercent, request.TiltDegree, 0m, DateTimeOffset.UtcNow)));
    }
}

/// <summary>SYNTHETIC climate provider for tests; values are invented and labeled as such.</summary>
public sealed class FakeClimateContextProvider : IClimateContextProvider
{
    private int _calls;

    public ClimateSettings Settings { get; } = new("NASA POWER", "power-climatology-v2", 2001, 2020, "RE", ["ALLSKY_SFC_SW_DWN", "T2M", "PRECTOTCORR"]);

    public Func<decimal, decimal, CancellationToken, Task<ClimateContextResult>>? Handler { get; set; }

    public int Calls => _calls;

    public async Task<ClimateContextResult> GetMonthlyClimatologyAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Handler is null ? Synthetic(latitude, longitude) : await Handler(latitude, longitude, cancellationToken);
    }

    public static ClimateContextResult Synthetic(decimal latitude, decimal longitude)
    {
        var monthly = Enumerable.Range(1, 12).Select(m => new ClimateMonthly(m, 30m, 5m, 150m, 27m, 3m, 90m)).ToList();
        return ClimateContextResult.Success(new ClimateContext(monthly, 1800m, 1080m, 27m,
            new ClimateMetadata("NASA POWER", "SYNTHETIC", 2001, 2020, "RE", ["SYNTHETIC"], latitude, longitude,
                new Dictionary<string, string> { ["PRECTOTCORR"] = "mm/day" }, DateTimeOffset.UtcNow)));
    }
}
