using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Modules.SolarSimulation.Regional;

/// <summary>Section "SolarSimulation:RegionalBaseline".</summary>
public sealed class RegionalBaselineOptions
{
    /// <summary>When false, energy and climate are UNAVAILABLE (REGIONAL_BASELINE_DISABLED); there is never a fallback to a single point.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dataset directory, relative to the application base directory (or absolute).</summary>
    public string DatasetDirectory { get; set; } = "ReferenceData/RegionalBaseline/hcm-79-2025-equal-area-20km-v1";

    /// <summary>Process-wide cap on concurrent PVGIS sample requests (PVGIS resets connections under heavier parallel load).</summary>
    public int MaxConcurrentPvgisRequests { get; set; } = 4;

    /// <summary>Process-wide cap on concurrent NASA POWER sample requests.</summary>
    public int MaxConcurrentNasaRequests { get; set; } = 3;

    /// <summary>
    /// Minimum share (0..1] of the published area weight whose samples must succeed. Below it the baseline FAILS
    /// (REGIONAL_COVERAGE_INSUFFICIENT); at or above it but below 1 it is DEGRADED (renormalised over the succeeded samples, warned, never reused).
    /// </summary>
    public decimal MinCoveredWeightFraction { get; set; } = 0.95m;

    /// <summary>How long a COMPLETE aggregate is kept in the shared cache (hours). DEGRADED or failed aggregates are never cached.</summary>
    public int CacheHours { get; set; } = 168;

    /// <summary>Compute the (configuration-independent) climate aggregate in the background at startup.</summary>
    public bool WarmClimateOnStartup { get; set; } = true;

    /// <summary>
    /// Time budget of one shared aggregate computation (seconds). It runs independently of any single caller, so a caller that stops
    /// waiting (for example the advisor's shorter timeout) does not cancel it; its successful COMPLETE result is cached for the next caller.
    /// </summary>
    public int ComputationBudgetSeconds { get; set; } = 25;
}

public enum RegionMembershipStatus
{
    InRegion,
    OutOfRegion,
    Unknown,
}

public static class RegionMembershipBases
{
    /// <summary>The site's official 2025 province code.</summary>
    public const string ProvinceCode = "PROVINCE_CODE";
    /// <summary>No province code: the site coordinates were tested against the verified (simplified) boundary.</summary>
    public const string SiteCoordinates = "SITE_COORDINATES";
    public const string None = "NONE";
}

/// <summary>Whether the regional model applies to a site. The site's own coordinates and commune are never replaced by regional values.</summary>
public sealed record RegionMembership(RegionMembershipStatus Status, string Basis)
{
    public bool InRegion => Status == RegionMembershipStatus.InRegion;
}

public sealed record RegionalPvRequest(decimal SystemLossPercent, decimal TiltDegree, decimal CompassAzimuthDegree, string MountingPlace);

public sealed record RegionalPvResult(RegionalPvBaseline? Baseline, ProviderFailure? Failure, RegionalCoverage? Coverage)
{
    public bool Succeeded => Baseline is not null;
}

public sealed record RegionalClimateResult(RegionalClimateBaseline? Baseline, ProviderFailure? Failure, RegionalCoverage? Coverage)
{
    public bool Succeeded => Baseline is not null;
}

/// <summary>The values of the regional model that change results; they go into every simulation fingerprint.</summary>
public sealed record RegionalFingerprint(string AlgorithmVersion, string DatasetKey, string ManifestSha256, string SamplesSha256, int SampleCount,
    decimal ReferenceKwp, decimal MinCoveredWeightFraction, PvEnergySettings PvSettings, ClimateSettings ClimateSettings);

/// <summary>
/// One city-wide solar / climate baseline for the 2025 territory of Ho Chi Minh City (province 79), used by simulations and the solar
/// advisor alike. It never calls a provider at a customer's coordinates.
/// </summary>
public interface IRegionalSolarBaselineProvider
{
    bool Enabled { get; }

    /// <summary>Null when disabled.</summary>
    RegionalDataset? Dataset { get; }

    RegionalFingerprint? Fingerprint { get; }

    RegionMembership ResolveMembership(string? provinceCode, decimal? latitude, decimal? longitude);

    /// <summary>Never throws for provider problems; honours cancellation (the caller's budget).</summary>
    Task<RegionalPvResult> GetPvBaselineAsync(RegionalPvRequest request, CancellationToken cancellationToken);

    /// <summary>Never throws for provider problems; honours cancellation.</summary>
    Task<RegionalClimateResult> GetClimateBaselineAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Process-wide state of the regional model: the validated dataset, the provider concurrency gates and the in-flight computations
/// (identical concurrent requests share one computation). Singleton.
/// </summary>
public sealed class RegionalBaselineRuntime : IDisposable
{
    public RegionalBaselineRuntime(RegionalBaselineOptions options, RegionalDataset? dataset)
    {
        if (options.Enabled && dataset is null) throw new ArgumentException("An enabled regional baseline needs a dataset.", nameof(dataset));
        Options = options;
        Dataset = options.Enabled ? dataset : null;
        PvgisGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPvgisRequests));
        NasaGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentNasaRequests));
    }

    public RegionalBaselineOptions Options { get; }
    public RegionalDataset? Dataset { get; }
    public SemaphoreSlim PvgisGate { get; }
    public SemaphoreSlim NasaGate { get; }
    internal ConcurrentDictionary<string, Lazy<Task<object>>> InFlight { get; } = new();

    /// <summary>Loads the configured dataset (fail fast when enabled and invalid) and builds the runtime.</summary>
    public static RegionalBaselineRuntime Create(RegionalBaselineOptions options, string baseDirectory)
    {
        if (!options.Enabled) return new RegionalBaselineRuntime(options, null);
        var directory = Path.IsPathRooted(options.DatasetDirectory) ? options.DatasetDirectory : Path.Combine(baseDirectory, options.DatasetDirectory);
        return new RegionalBaselineRuntime(options, RegionalDataset.Load(directory));
    }

    public void Dispose()
    {
        PvgisGate.Dispose();
        NasaGate.Dispose();
    }
}

/// <summary>
/// With an <see cref="IServiceScopeFactory"/> (dependency injection) every shared computation resolves its own provider clients in its
/// own scope, so it never uses a client disposed with the request that started it. Without one (unit tests) the given clients are used.
/// </summary>
public sealed class RegionalSolarBaselineProvider(
    RegionalBaselineRuntime runtime,
    IPvEnergyEstimator pvgis,
    IClimateContextProvider climate,
    ICacheStore cache,
    TimeProvider time,
    ILogger<RegionalSolarBaselineProvider> logger,
    IServiceScopeFactory? scopes = null) : IRegionalSolarBaselineProvider
{
    /// <summary>The common reference system: PVGIS energy is linear in peak power, so 1 kWp gives the specific yield.</summary>
    public const decimal ReferenceKwp = 1m;

    public bool Enabled => runtime.Dataset is not null;

    public RegionalDataset? Dataset => runtime.Dataset;

    public RegionalFingerprint? Fingerprint => runtime.Dataset is { } d
        ? new RegionalFingerprint(Constants.AlgorithmVersions.RegionalBaseline, d.DatasetKey, d.ManifestSha256, d.SamplesSha256, d.Samples.Count, ReferenceKwp,
            runtime.Options.MinCoveredWeightFraction, pvgis.Settings, climate.Settings)
        : null;

    public RegionMembership ResolveMembership(string? provinceCode, decimal? latitude, decimal? longitude)
    {
        if (runtime.Dataset is not { } d) return new(RegionMembershipStatus.Unknown, RegionMembershipBases.None);
        if (!string.IsNullOrWhiteSpace(provinceCode))
            return new(provinceCode.Trim() == d.RegionCode ? RegionMembershipStatus.InRegion : RegionMembershipStatus.OutOfRegion, RegionMembershipBases.ProvinceCode);
        if (latitude is { } lat && longitude is { } lon)
            return new(d.Contains(lat, lon) ? RegionMembershipStatus.InRegion : RegionMembershipStatus.OutOfRegion, RegionMembershipBases.SiteCoordinates);
        return new(RegionMembershipStatus.Unknown, RegionMembershipBases.None);
    }

    public async Task<RegionalPvResult> GetPvBaselineAsync(RegionalPvRequest request, CancellationToken cancellationToken)
    {
        if (runtime.Dataset is not { } dataset)
            return new(null, new ProviderFailure(RegionalFailureCodes.Disabled, "The regional solar baseline is disabled."), null);

        var aspect = Math.Round((decimal)AzimuthConvention.ToPvgisAspect((double)request.CompassAzimuthDegree), 2);
        var s = pvgis.Settings;
        var key = string.Join(':', "solar", "regional-pv", Constants.AlgorithmVersions.RegionalBaseline, dataset.DatasetKey, dataset.ManifestSha256[..16],
            s.ProviderVersion, s.Technology, s.UseHorizon ? "h1" : "h0", Inv(request.SystemLossPercent), Inv(request.TiltDegree), Inv(aspect), request.MountingPlace,
            Inv(runtime.Options.MinCoveredWeightFraction));

        var cached = await TryGetAsync<RegionalPvBaseline>(key, cancellationToken);
        if (cached is not null) return new(cached, null, cached.Coverage);

        return (RegionalPvResult)await SharedAsync(key, async (pv, _, budget) =>
        {
            var results = await Task.WhenAll(dataset.Samples.Select(sample => EstimateSampleAsync(pv, sample, request, budget)));
            var baseline = RegionalAggregationCalculator.AggregatePv(results, ReferenceKwp, runtime.Options.MinCoveredWeightFraction, time.GetUtcNow(), out var coverage);
            if (baseline is null)
            {
                var codes = results.Where(r => r.Failure is not null).GroupBy(r => r.Failure!.Code).Select(g => $"{g.Key}×{g.Count()}");
                logger.LogWarning("Regional PVGIS baseline unavailable: {Succeeded}/{Total} samples, covered weight {Covered} (failures: {Failures}).",
                    coverage.SucceededSampleCount, coverage.SampleCount, coverage.CoveredWeightFraction, string.Join(", ", codes));
                var timedOut = results.Any(r => r.Failure?.Code == ProviderFailureCodes.Timeout);
                return new RegionalPvResult(null, new ProviderFailure(timedOut ? ProviderFailureCodes.Timeout : RegionalFailureCodes.CoverageInsufficient,
                    $"Only {coverage.SucceededSampleCount} of {coverage.SampleCount} regional samples ({coverage.CoveredWeightFraction:P1} of the area) returned a PVGIS estimate."),
                    coverage);
            }
            if (baseline.Coverage.Quality == RegionalQualities.Complete) await TrySetAsync(key, baseline, budget);
            return new RegionalPvResult(baseline, null, baseline.Coverage);
        }, cancellationToken);
    }

    public async Task<RegionalClimateResult> GetClimateBaselineAsync(CancellationToken cancellationToken)
    {
        if (runtime.Dataset is not { } dataset)
            return new(null, new ProviderFailure(RegionalFailureCodes.Disabled, "The regional climate baseline is disabled."), null);

        var c = climate.Settings;
        var key = string.Join(':', "solar", "regional-climate", Constants.AlgorithmVersions.RegionalBaseline, dataset.DatasetKey, dataset.ManifestSha256[..16],
            c.ApiTag, $"{c.StartYear}-{c.EndYear}", c.Community, string.Join(',', c.Parameters), Inv(runtime.Options.MinCoveredWeightFraction));
        var cached = await TryGetAsync<RegionalClimateBaseline>(key, cancellationToken);
        if (cached is not null) return new(cached, null, cached.Coverage);

        return (RegionalClimateResult)await SharedAsync(key, async (_, nasa, budget) =>
        {
            var results = await Task.WhenAll(dataset.Samples.Select(sample => ClimateSampleAsync(nasa, sample, budget)));
            var baseline = RegionalAggregationCalculator.AggregateClimate(results, runtime.Options.MinCoveredWeightFraction, time.GetUtcNow(), out var coverage);
            if (baseline is null)
            {
                logger.LogWarning("Regional NASA POWER baseline unavailable: {Succeeded}/{Total} samples, covered weight {Covered}.",
                    coverage.SucceededSampleCount, coverage.SampleCount, coverage.CoveredWeightFraction);
                var timedOut = results.Any(r => r.Failure?.Code == ProviderFailureCodes.Timeout);
                return new RegionalClimateResult(null, new ProviderFailure(timedOut ? ProviderFailureCodes.Timeout : RegionalFailureCodes.CoverageInsufficient,
                    $"Only {coverage.SucceededSampleCount} of {coverage.SampleCount} regional samples returned NASA POWER climatology."), coverage);
            }
            if (baseline.Coverage.Quality == RegionalQualities.Complete) await TrySetAsync(key, baseline, budget);
            return new RegionalClimateResult(baseline, null, baseline.Coverage);
        }, cancellationToken);
    }

    private async Task<RegionalPvSampleResult> EstimateSampleAsync(IPvEnergyEstimator pv, RegionalSample sample, RegionalPvRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.PvgisGate.WaitAsync(cancellationToken);
            try
            {
                var result = await pv.EstimateAsync(new PvEnergyRequest(sample.Latitude, sample.Longitude, ReferenceKwp, request.SystemLossPercent,
                    request.TiltDegree, request.CompassAzimuthDegree, request.MountingPlace), cancellationToken);
                return new(sample, result.Estimate, result.Failure);
            }
            finally
            {
                runtime.PvgisGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new(sample, null, new ProviderFailure(ProviderFailureCodes.Timeout, "The provider time budget was exceeded."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Unexpected PVGIS failure for regional sample {LocationId}.", sample.LocationId);
            return new(sample, null, new ProviderFailure(ProviderFailureCodes.Unavailable, "PVGIS failed unexpectedly."));
        }
    }

    private async Task<RegionalClimateSampleResult> ClimateSampleAsync(IClimateContextProvider nasa, RegionalSample sample, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.NasaGate.WaitAsync(cancellationToken);
            try
            {
                var result = await nasa.GetMonthlyClimatologyAsync(sample.Latitude, sample.Longitude, cancellationToken);
                return new(sample, result.Context, result.Failure);
            }
            finally
            {
                runtime.NasaGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new(sample, null, new ProviderFailure(ProviderFailureCodes.Timeout, "The provider time budget was exceeded."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Unexpected NASA POWER failure for regional sample {LocationId}.", sample.LocationId);
            return new(sample, null, new ProviderFailure(ProviderFailureCodes.Unavailable, "NASA POWER failed unexpectedly."));
        }
    }

    /// <summary>
    /// Identical concurrent requests on this instance share one computation with its own budget
    /// (<see cref="RegionalBaselineOptions.ComputationBudgetSeconds"/>), independent of every caller; the entry is removed when it finishes
    /// (so a failure is recomputed next time). A caller's cancellation only stops that caller's wait.
    /// </summary>
    private async Task<object> SharedAsync(string key, Func<IPvEnergyEstimator, IClimateContextProvider, CancellationToken, Task<object>> compute,
        CancellationToken cancellationToken)
    {
        Lazy<Task<object>>? candidate = null;
        // On the thread pool: the shared computation never depends on (or blocks) the first caller's synchronisation context.
        candidate = new Lazy<Task<object>>(() => Task.Run(async () =>
        {
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(runtime.Options.ComputationBudgetSeconds, 5, 120)));
                if (scopes is null) return await compute(pvgis, climate, budget.Token).ConfigureAwait(false);
                await using var scope = scopes.CreateAsyncScope();
                return await compute(scope.ServiceProvider.GetRequiredService<IPvEnergyEstimator>(),
                    scope.ServiceProvider.GetRequiredService<IClimateContextProvider>(), budget.Token).ConfigureAwait(false);
            }
            finally
            {
                runtime.InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, candidate!));
            }
        }), LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = runtime.InFlight.GetOrAdd(key, candidate);
        return await flight.Value.WaitAsync(cancellationToken);
    }

    private async Task<T?> TryGetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await cache.GetAsync<T>(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Regional baseline cache read failed.");
            return null;
        }
    }

    private async Task TrySetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetAsync(key, value, TimeSpan.FromHours(Math.Max(1, runtime.Options.CacheHours)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Regional baseline cache write failed.");
        }
    }

    private static string Inv(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
