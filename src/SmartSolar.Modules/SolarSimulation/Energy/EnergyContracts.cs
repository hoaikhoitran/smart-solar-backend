namespace SmartSolar.Modules.SolarSimulation.Energy;

public static class ProviderFailureCodes
{
    public const string Timeout = "PROVIDER_TIMEOUT";
    public const string Unavailable = "PROVIDER_UNAVAILABLE";
    public const string RateLimited = "PROVIDER_RATE_LIMITED";
    public const string RejectedRequest = "PROVIDER_REJECTED_REQUEST";
    public const string ResponseInvalid = "PROVIDER_RESPONSE_INVALID";
    public const string LocationNotCovered = "LOCATION_NOT_COVERED";
}

public sealed record ProviderFailure(string Code, string Message);

/// <summary>PVGIS "mountingplace" values as defined in the PVGIS 5 user manual.</summary>
public static class PvgisMountingPlaces
{
    /// <summary>"Modules are mounted on a rack with air flowing freely behind the modules."</summary>
    public const string Free = "free";

    /// <summary>"Modules are completely built into the structure of the wall or roof of a building, with no air movement behind the modules."</summary>
    public const string Building = "building";
}

/// <summary>Provider settings that change results and therefore belong in the fingerprint.</summary>
public sealed record PvEnergySettings(string Provider, string ProviderVersion, string Technology, bool UseHorizon);

public sealed record PvEnergyRequest(
    decimal Latitude,
    decimal Longitude,
    decimal PeakPowerKwp,
    decimal SystemLossPercent,
    decimal TiltDegree,
    decimal CompassAzimuthDegree,
    string MountingPlace);

public sealed record PvMonthlyEnergy(
    int Month,
    decimal EnergyKwh,
    decimal? StdDevKwh,
    decimal? InPlaneIrradiationKwhPerM2);

/// <summary>Metadata exactly as returned or sent, so the estimate stays explainable and reproducible.</summary>
public sealed record PvProviderMetadata(
    string Provider,
    string ProviderVersion,
    string? RadiationDatabase,
    string? MeteoDatabase,
    int? YearMin,
    int? YearMax,
    string? HorizonDatabase,
    string MountingPlace,
    string Technology,
    bool UseHorizon,
    decimal PeakPowerKwp,
    decimal SystemLossPercent,
    decimal AngleSentDegree,
    decimal AspectSentDegree,
    DateTimeOffset RetrievedAt);

/// <summary>Typical-year (historical) estimate. Units: kWh per month / per year.</summary>
public sealed record PvEnergyEstimate(
    decimal AnnualEnergyKwh,
    decimal? AnnualStdDevKwh,
    decimal? AnnualInPlaneIrradiationKwhPerM2,
    decimal? TotalLossPercent,
    IReadOnlyList<PvMonthlyEnergy> Monthly,
    PvProviderMetadata Provider);

public sealed record PvEnergyResult(PvEnergyEstimate? Estimate, ProviderFailure? Failure)
{
    public bool Succeeded => Estimate is not null;

    public static PvEnergyResult Success(PvEnergyEstimate estimate) => new(estimate, null);

    public static PvEnergyResult Fail(string code, string message) => new(null, new ProviderFailure(code, message));
}

public interface IPvEnergyEstimator
{
    PvEnergySettings Settings { get; }

    /// <summary>Never throws for provider problems; returns a failure instead. Honors cancellation.</summary>
    Task<PvEnergyResult> EstimateAsync(PvEnergyRequest request, CancellationToken cancellationToken);
}

public sealed record ClimateSettings(string Provider, string ApiTag, int StartYear, int EndYear, string Community, IReadOnlyList<string> Parameters);

/// <summary>
/// One calendar month of historical climatology. Daily-rate values are the provider's monthly
/// mean of daily values; totals multiply them by the mean month length over the period.
/// </summary>
public sealed record ClimateMonthly(
    int Month,
    decimal DaysInMonthUsed,
    decimal? IrradiationKwhPerM2PerDay,
    decimal? IrradiationKwhPerM2,
    decimal? TemperatureC,
    decimal? PrecipitationMmPerDay,
    decimal? PrecipitationMm);

public sealed record ClimateMetadata(
    string Provider,
    string? ApiVersion,
    int StartYear,
    int EndYear,
    string Community,
    IReadOnlyList<string> Sources,
    decimal RequestedLatitude,
    decimal RequestedLongitude,
    IReadOnlyDictionary<string, string> ParameterUnits,
    DateTimeOffset RetrievedAt);

public sealed record ClimateContext(
    IReadOnlyList<ClimateMonthly> Monthly,
    decimal? AnnualIrradiationKwhPerM2,
    decimal? AnnualPrecipitationMm,
    decimal? AnnualMeanTemperatureC,
    ClimateMetadata Metadata);

public sealed record ClimateContextResult(ClimateContext? Context, ProviderFailure? Failure)
{
    public bool Succeeded => Context is not null;

    public static ClimateContextResult Success(ClimateContext context) => new(context, null);

    public static ClimateContextResult Fail(string code, string message) => new(null, new ProviderFailure(code, message));
}

public interface IClimateContextProvider
{
    ClimateSettings Settings { get; }

    /// <summary>Never throws for provider problems; returns a failure instead. Honors cancellation.</summary>
    Task<ClimateContextResult> GetMonthlyClimatologyAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken);
}
