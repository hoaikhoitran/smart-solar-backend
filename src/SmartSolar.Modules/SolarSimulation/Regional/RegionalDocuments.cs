using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.Regional;

/// <summary>Builds the response metadata of the regional model (formula, provenance, coverage, years, timestamps).</summary>
public static class RegionalDocuments
{
    public const string PvFormula =
        "Y_HCM[m] = Σ_i w_i · E_PVGIS[m,i] / 1 kWp over the regional samples (w_i = clipped equal-area cell area / total area, renormalised over the "
        + "succeeded samples only when the result is DEGRADED); E_site[m] = Y_HCM[m] × installed kWp (rounded to 0.01 kWh); annual = Σ_m E_site[m]; "
        + "local shading (when applied) multiplies each month once by (1 − monthly loss fraction of the site's own geometry).";

    public const string ClimateFormula =
        "C_HCM[m] = Σ_i w_i · C_NASA[m,i] for each daily-rate metric (kWh/m²/day, mm/day) and for °C; monthly totals = daily rate × mean days of the "
        + "month over the configured period; annual totals = Σ monthly totals; annual temperature = Σ_i w_i · ANN_i.";

    public static readonly IReadOnlyList<string> PvLimitations =
    [
        "SmartSolar's derived regional average: neither PVGIS nor NASA POWER publishes an official 'Ho Chi Minh City average'.",
        "The same regional climate baseline is applied to every site in the 2025 territory (former Ho Chi Minh City, Binh Duong and Ba Ria – Vung Tau); local differences in irradiance are not represented.",
        "Per-roof inputs (usable surface, obstacles, panel product and count, tilt, azimuth, mounting, losses and shading) still change the site result.",
        "Grid sampling error versus a 5 km grid was below 0.25 % monthly and 0.13 % annually in the documented convergence check; PVGIS's own uncertainty is larger.",
        "Typical-year values from historical data; not a forecast for a specific year and not a measurement of this roof.",
    ];

    public static readonly IReadOnlyList<string> ClimateLimitations =
    [
        "Historical monthly means for the stated period; not measured weather after the period and not a forecast.",
        "Context only: never added to, or multiplied with, the PVGIS energy estimate.",
        "NASA POWER grid cells are about 0.5° × 0.625°; several samples can share one provider cell (they keep their own area weight).",
    ];

    public static RegionalEstimateDocument ForPv(RegionalDataset dataset, RegionalPvBaseline baseline, RegionMembership membership)
        => Build(dataset, baseline.Coverage, baseline.ReferenceKwp, baseline.RadiationDatabases, baseline.ProviderYearMin, baseline.ProviderYearMax,
            baseline.ProviderRetrievedFrom, baseline.ProviderRetrievedTo, baseline.ComputedAt, membership, PvFormula, PvLimitations);

    public static RegionalEstimateDocument ForClimate(RegionalDataset dataset, RegionalClimateBaseline baseline, RegionMembership membership)
    {
        var meta = baseline.Context.Metadata;
        return Build(dataset, baseline.Coverage, null, meta.Sources, meta.StartYear, meta.EndYear, null, meta.RetrievedAt, baseline.ComputedAt, membership,
            ClimateFormula, ClimateLimitations);
    }

    private static RegionalEstimateDocument Build(RegionalDataset d, RegionalCoverage coverage, decimal? referenceKwp, IReadOnlyList<string> databases,
        int? yearMin, int? yearMax, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset computedAt, RegionMembership membership, string formula,
        IReadOnlyList<string> limitations)
        => new(
            EstimateBases.HcmcRegionalAreaWeighted,
            EstimateBases.LabelVi,
            d.RegionCode,
            d.RegionName,
            Constants.AlgorithmVersions.RegionalBaseline,
            d.DatasetKey,
            d.SourceDatasetVersion,
            d.ManifestSha256,
            d.SamplesSha256,
            d.WeightMethod,
            d.CellKm,
            coverage.SampleCount,
            coverage.SucceededSampleCount,
            coverage.CoveredWeightFraction,
            coverage.Quality,
            coverage.MissingLocationIds,
            referenceKwp,
            databases,
            yearMin,
            yearMax,
            from,
            to,
            computedAt,
            membership.Basis,
            d.ReferencePoint,
            new RegionalBoundaryDocument(d.Boundary.Publisher, d.Boundary.DatasetPage, d.Boundary.DownloadUrl, d.Boundary.FileSha256, d.Boundary.FeaturePcode,
                d.Boundary.ValidOn, d.Boundary.License, d.Boundary.Crs),
            formula,
            limitations);
}
