using System.Text.Json;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

internal static class TravelEstimateViews
{
    public const string HistoricalKind = "HISTORICAL_ESTIMATE";
    public const string CongestionAvailable = "AVAILABLE";
    public const string CongestionNotReturned = "NOT_RETURNED_BY_PROVIDER";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TravelEstimateView From(TechnicianTravelEstimate e) => new(
        e.Provider, e.ProviderVersion, e.Vehicle, e.OriginKind, e.OriginAppointmentId,
        e.OriginLatitude, e.OriginLongitude, e.DestinationLatitude, e.DestinationLongitude,
        e.DepartureAt, e.EstimatedAt, e.DistanceMeters, e.DurationSeconds, e.SafetyBufferMinutes, e.EstimatedArrivalAt, e.Status,
        Congestion(ParseStored(e.CongestionSummary)),
        HistoricalKind);

    public static TravelEstimateView From(RequiredLeg leg, TravelEstimateResult result, DateTimeOffset departure, int bufferMinutes) => new(
        result.Provider, result.ProviderVersion, result.Vehicle, leg.OriginKind, leg.OriginAppointmentId,
        leg.Origin.Latitude, leg.Origin.Longitude, leg.Destination.Latitude, leg.Destination.Longitude,
        departure, result.EstimatedAtUtc,
        result.DistanceMeters is { } m ? Math.Round((decimal)m, 1) : null,
        result.Duration is { } d ? (int)Math.Ceiling(d.TotalSeconds) : null,
        bufferMinutes,
        result.Duration is { } duration ? departure + duration : null,
        result.Status,
        Congestion(result.Congestion),
        HistoricalKind);

    public static string? Serialize(CongestionSummary? summary)
        => summary is null ? null : JsonSerializer.Serialize(new StoredCongestion(summary.SegmentsByLevel.ToDictionary(), summary.HeavyOrWorseMeters,
            summary.MetersByLevel?.ToDictionary()), Json);

    private static TravelCongestionView Congestion(CongestionSummary? summary)
        => summary is null
            ? new TravelCongestionView(CongestionNotReturned, null, null, null)
            : new TravelCongestionView(CongestionAvailable, summary.SegmentsByLevel, summary.HeavyOrWorseMeters, summary.MetersByLevel);

    /// <summary>Reads the stored snapshot; anything unreadable (for example an older format) counts as not returned.</summary>
    private static CongestionSummary? ParseStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCongestion>(json, Json);
            return stored?.SegmentsByLevel is { Count: > 0 } levels ? new CongestionSummary(levels, stored.HeavyOrWorseMeters, stored.MetersByLevel) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredCongestion(Dictionary<string, int>? SegmentsByLevel, double? HeavyOrWorseMeters, Dictionary<string, double>? MetersByLevel);
}
