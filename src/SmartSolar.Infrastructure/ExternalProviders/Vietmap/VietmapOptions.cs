namespace SmartSolar.Infrastructure.ExternalProviders.Vietmap;

/// <summary>
/// Section "Providers:Vietmap" (environment: Providers__Vietmap__*). The API key is read from
/// configuration only and is never logged or returned to clients.
/// </summary>
public sealed class VietmapOptions
{
    public const string SectionName = "Providers:Vietmap";
    public const string RequiredVehicle = "motorcycle";

    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://maps.vietmap.vn";

    /// <summary>V1 routes every technician as a motorcycle; any other value is rejected at startup.</summary>
    public string Vehicle { get; set; } = RequiredVehicle;

    public int AttemptTimeoutMilliseconds { get; set; } = 8_000;
    public int MaxRetryAttempts { get; set; } = 1;
    public int RetryBaseDelayMilliseconds { get; set; } = 300;
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Successful estimates are cached this long (short-lived; provider data is not retained indefinitely).</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>Departure times are floored to this bucket for the provider "time" parameter and the cache key.</summary>
    public int DepartureBucketMinutes { get; set; } = 15;

    /// <summary>Ask for congestion annotations to build a small per-level distance summary.</summary>
    public bool RequestCongestion { get; set; } = true;

    /// <summary>Values from a .env file may keep their surrounding quotes; they are not part of the value.</summary>
    public static string? Clean(string? value) => value?.Trim().Trim('"', '\'').Trim();
}
