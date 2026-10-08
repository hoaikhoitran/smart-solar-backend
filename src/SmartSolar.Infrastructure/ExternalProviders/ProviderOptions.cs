namespace SmartSolar.Infrastructure.ExternalProviders;

/// <summary>PVGIS 5.3 non-interactive API (PVcalc). Section "Providers:Pvgis".</summary>
public sealed class PvgisOptions
{
    public const string SectionName = "Providers:Pvgis";

    public string BaseUrl { get; set; } = "https://re.jrc.ec.europa.eu/api/v5_3/";
    public string ProviderVersion { get; set; } = "5.3";

    /// <summary>PVGIS default technology; Catalog has no technology field in V1.</summary>
    public string Technology { get; set; } = "crystSi";

    /// <summary>PVGIS default: account for terrain horizon (not local obstacles).</summary>
    public bool UseHorizon { get; set; } = true;

    public int AttemptTimeoutMilliseconds { get; set; } = 10_000;
    public int MaxRetryAttempts { get; set; } = 2;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    public int CacheHours { get; set; } = 168;
}

/// <summary>NASA POWER climatology point API. Section "Providers:NasaPower".</summary>
public sealed class NasaPowerOptions
{
    public const string SectionName = "Providers:NasaPower";

    public string BaseUrl { get; set; } = "https://power.larc.nasa.gov/api/temporal/climatology/point";
    public string Community { get; set; } = "RE";
    public int StartYear { get; set; } = 2001;
    public int EndYear { get; set; } = 2020;
    public string ApiTag { get; set; } = "power-climatology-v2";

    public int AttemptTimeoutMilliseconds { get; set; } = 10_000;
    public int MaxRetryAttempts { get; set; } = 2;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    public int CacheHours { get; set; } = 720;
}
