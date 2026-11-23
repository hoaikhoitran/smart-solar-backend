using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Modules.SolarSimulation.Regional;

/// <summary>Monthly regional specific yield of the 1 kWp reference system (kWh/kWp per calendar month of the typical year).</summary>
public sealed record RegionalMonthlyYield(int Month, decimal SpecificYieldKwhPerKwp, decimal? InPlaneIrradiationKwhPerM2);

/// <summary>Which samples entered an aggregate. <see cref="CoveredWeightFraction"/> is the published weight of the succeeded samples.</summary>
public sealed record RegionalCoverage(int SampleCount, int SucceededSampleCount, decimal CoveredWeightFraction, string Quality, IReadOnlyList<string> MissingLocationIds);

/// <summary>
/// City-wide PVGIS baseline for one reference configuration (1 kWp, tilt, aspect, loss, mounting place, technology, horizon):
/// <c>Y_HCM[m] = Σ_i w̃_i · E_PVGIS[m, i] / referenceKwp</c> with <c>w̃_i = w_i / Σ_succeeded w</c>.
/// </summary>
public sealed record RegionalPvBaseline(
    IReadOnlyList<RegionalMonthlyYield> Monthly,
    decimal AnnualSpecificYieldKwhPerKwp,
    decimal ReferenceKwp,
    RegionalCoverage Coverage,
    PvProviderMetadata ReferenceRequest,
    IReadOnlyList<string> RadiationDatabases,
    int? ProviderYearMin,
    int? ProviderYearMax,
    DateTimeOffset ProviderRetrievedFrom,
    DateTimeOffset ProviderRetrievedTo,
    DateTimeOffset ComputedAt);

public sealed record RegionalClimateBaseline(ClimateContext Context, RegionalCoverage Coverage, DateTimeOffset ComputedAt);

public sealed record RegionalPvSampleResult(RegionalSample Sample, PvEnergyEstimate? Estimate, ProviderFailure? Failure);

public sealed record RegionalClimateSampleResult(RegionalSample Sample, ClimateContext? Context, ProviderFailure? Failure);

/// <summary>
/// Pure, unit-aware weighted aggregation. Nothing is invented: a sample without a complete provider result counts as missing, and the
/// aggregate exists only when the succeeded samples cover at least the configured share of the published area weight.
/// </summary>
public static class RegionalAggregationCalculator
{
    private const decimal FullCoverageTolerance = 0.000001m;

    /// <summary>The weighted PVGIS baseline, or null with <paramref name="coverage"/> describing why (never a renormalised guess below the threshold).</summary>
    public static RegionalPvBaseline? AggregatePv(IReadOnlyList<RegionalPvSampleResult> results, decimal referenceKwp, decimal minCoveredWeightFraction,
        DateTimeOffset computedAt, out RegionalCoverage coverage)
    {
        if (referenceKwp <= 0) throw new ArgumentOutOfRangeException(nameof(referenceKwp));
        var ok = results.Where(r => r.Estimate is { Monthly.Count: 12 } e && e.Monthly.Select(m => m.Month).Distinct().Count() == 12
                                     && e.Monthly.All(m => m.EnergyKwh >= 0)).ToList();
        coverage = Coverage(results.Select(r => r.Sample).ToList(), ok.Select(r => r.Sample).ToList());
        if (ok.Count == 0 || coverage.CoveredWeightFraction + FullCoverageTolerance < minCoveredWeightFraction) return null;

        var covered = ok.Sum(r => r.Sample.Weight);
        var monthly = new List<RegionalMonthlyYield>(12);
        for (var month = 1; month <= 12; month++)
        {
            decimal yield = 0m;
            decimal? irradiation = 0m;
            foreach (var r in ok)
            {
                var m = r.Estimate!.Monthly.Single(x => x.Month == month);
                var w = r.Sample.Weight / covered;
                yield += w * m.EnergyKwh / referenceKwp;
                irradiation = irradiation is { } acc && m.InPlaneIrradiationKwhPerM2 is { } h ? acc + w * h : null;
            }
            monthly.Add(new RegionalMonthlyYield(month, yield, irradiation is { } ir ? Math.Round(ir, 3) : null));
        }

        var providers = ok.Select(r => r.Estimate!.Provider).ToList();
        return new RegionalPvBaseline(
            monthly,
            monthly.Sum(m => m.SpecificYieldKwhPerKwp),
            referenceKwp,
            coverage,
            providers[0] with { RetrievedAt = providers.Max(p => p.RetrievedAt) },
            providers.Select(p => p.RadiationDatabase).Where(d => d is not null).Select(d => d!).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList(),
            providers.All(p => p.YearMin is not null) ? providers.Min(p => p.YearMin) : null,
            providers.All(p => p.YearMax is not null) ? providers.Max(p => p.YearMax) : null,
            providers.Min(p => p.RetrievedAt),
            providers.Max(p => p.RetrievedAt),
            computedAt);
    }

    /// <summary>
    /// Site energy from the regional baseline: <c>E_site[m] = Y_HCM[m] × installedCapacityKwp</c> (kWh, rounded to 0.01 per month); the annual
    /// value is the sum of the rounded months, so monthly and annual values always agree. Never averages raw production of different capacities.
    /// </summary>
    public static IReadOnlyList<PvMonthlyEnergy> ScaleToSite(RegionalPvBaseline baseline, decimal installedCapacityKwp)
    {
        if (installedCapacityKwp < 0) throw new ArgumentOutOfRangeException(nameof(installedCapacityKwp));
        return baseline.Monthly
            .Select(m => new PvMonthlyEnergy(m.Month, Math.Round(m.SpecificYieldKwhPerKwp * installedCapacityKwp, 2, MidpointRounding.AwayFromZero), null,
                m.InPlaneIrradiationKwhPerM2))
            .ToList();
    }

    /// <summary>
    /// Area-weighted climatology over the same samples and period. Daily-rate metrics (kWh/m²/day, mm/day) are averaged as daily rates and
    /// converted to monthly totals with the period's mean month length (identical for every sample, so the total of the mean equals the
    /// mean of the totals); temperature is averaged as °C; annual totals are the sums of the monthly totals. A sample with any missing
    /// (fill) value is treated as missing.
    /// </summary>
    public static RegionalClimateBaseline? AggregateClimate(IReadOnlyList<RegionalClimateSampleResult> results, decimal minCoveredWeightFraction,
        DateTimeOffset computedAt, out RegionalCoverage coverage)
    {
        var ok = results.Where(r => r.Context is { } c && c.Monthly.Count == 12 && c.AnnualMeanTemperatureC is not null
                                     && c.Monthly.All(m => m.IrradiationKwhPerM2PerDay is not null && m.TemperatureC is not null && m.PrecipitationMmPerDay is not null))
            .ToList();
        coverage = Coverage(results.Select(r => r.Sample).ToList(), ok.Select(r => r.Sample).ToList());
        if (ok.Count == 0 || coverage.CoveredWeightFraction + FullCoverageTolerance < minCoveredWeightFraction) return null;

        var covered = ok.Sum(r => r.Sample.Weight);
        var first = ok[0].Context!;
        var months = new List<ClimateMonthly>(12);
        for (var month = 1; month <= 12; month++)
        {
            decimal irr = 0, temp = 0, rain = 0;
            foreach (var r in ok)
            {
                var m = r.Context!.Monthly.Single(x => x.Month == month);
                var w = r.Sample.Weight / covered;
                irr += w * m.IrradiationKwhPerM2PerDay!.Value;
                temp += w * m.TemperatureC!.Value;
                rain += w * m.PrecipitationMmPerDay!.Value;
            }
            var days = first.Monthly.Single(x => x.Month == month).DaysInMonthUsed;
            irr = Math.Round(irr, 4, MidpointRounding.AwayFromZero);
            rain = Math.Round(rain, 4, MidpointRounding.AwayFromZero);
            months.Add(new ClimateMonthly(month, days, irr, ClimatologyConversion.MonthlyTotal(irr, days), Math.Round(temp, 3, MidpointRounding.AwayFromZero),
                rain, ClimatologyConversion.MonthlyTotal(rain, days)));
        }
        var annualTemperature = Math.Round(ok.Sum(r => r.Sample.Weight / covered * r.Context!.AnnualMeanTemperatureC!.Value), 3, MidpointRounding.AwayFromZero);
        var meta = first.Metadata;
        var metadata = new ClimateMetadata(meta.Provider, meta.ApiVersion, meta.StartYear, meta.EndYear, meta.Community,
            ok.SelectMany(r => r.Context!.Metadata.Sources).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            null, null, meta.ParameterUnits, ok.Max(r => r.Context!.Metadata.RetrievedAt));
        return new RegionalClimateBaseline(
            new ClimateContext(months, months.Sum(m => m.IrradiationKwhPerM2!.Value), months.Sum(m => m.PrecipitationMm!.Value), annualTemperature, metadata),
            coverage, computedAt);
    }

    private static RegionalCoverage Coverage(IReadOnlyList<RegionalSample> all, IReadOnlyList<RegionalSample> ok)
    {
        var covered = ok.Sum(s => s.Weight);
        var okIds = ok.Select(s => s.LocationId).ToHashSet(StringComparer.Ordinal);
        var missing = all.Where(s => !okIds.Contains(s.LocationId)).Select(s => s.LocationId).ToList();
        return new RegionalCoverage(all.Count, ok.Count, Math.Round(covered, 9),
            missing.Count == 0 ? RegionalQualities.Complete : RegionalQualities.Degraded, missing);
    }
}
