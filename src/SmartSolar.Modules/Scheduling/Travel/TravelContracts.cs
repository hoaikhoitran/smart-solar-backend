using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.Scheduling.Travel;

/// <summary>Outcome of one route estimate. Anything but <see cref="Ok"/> means "not verified".</summary>
public static class RouteCalculationStatuses
{
    public const string Ok = "OK";
    public const string RouteNotFound = "ROUTE_NOT_FOUND";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string RateLimited = "RATE_LIMITED";
    public const string Timeout = "TIMEOUT";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string InvalidResponse = "INVALID_RESPONSE";
    public const string RejectedRequest = "REJECTED_REQUEST";
    public const string ProviderDisabled = "PROVIDER_DISABLED";
    public const string BudgetExhausted = "BUDGET_EXHAUSTED";
}

/// <summary>A route question: from where, to where, departing when (UTC).</summary>
public sealed record TravelEstimateRequest(GeoPoint Origin, GeoPoint Destination, DateTimeOffset DepartureAtUtc);

/// <summary>
/// Provider congestion annotations, summarised without inventing anything. Vietmap Route v4 returns
/// <c>paths[].annotations.congestion</c> (one level per segment: low, moderate, heavy, severe, unknown)
/// and, when requested, <c>annotations.congestion_distance</c>. Live responses (verified 2026-10-12)
/// use objects <c>{ value, first, last }</c> and align the two arrays segment by segment, so meters per
/// level are known (<see cref="MetersByLevel"/>). When they are not aligned only the level counts and
/// the documented heavy-or-worse total are kept.
/// </summary>
public sealed record CongestionSummary(
    IReadOnlyDictionary<string, int> SegmentsByLevel,
    double? HeavyOrWorseMeters,
    IReadOnlyDictionary<string, double>? MetersByLevel = null);

/// <summary>
/// Minimal provider-neutral result. Durations are a historical/typical estimate made at
/// <see cref="EstimatedAtUtc"/> for the requested departure; they are not a traffic forecast.
/// </summary>
public sealed record TravelEstimateResult(
    string Status,
    string Provider,
    string ProviderVersion,
    string Vehicle,
    DateTimeOffset DepartureAtUtc,
    DateTimeOffset EstimatedAtUtc,
    double? DistanceMeters,
    TimeSpan? Duration,
    CongestionSummary? Congestion,
    string? Message)
{
    public bool IsOk => Status == RouteCalculationStatuses.Ok && Duration is not null && DistanceMeters is not null;
}

/// <summary>
/// Reusable travel-time capability for every scheduling consumer (pre-survey now; warranty,
/// maintenance and repair later). Implementations never throw for provider problems and never
/// return an invented duration: failures come back with a non-OK status.
/// </summary>
public interface ITravelTimeEstimator
{
    Task<TravelEstimateResult> EstimateAsync(TravelEstimateRequest request, CancellationToken cancellationToken);
}
