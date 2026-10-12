using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartSolar.Api.Contracts.Scheduling;

/// <summary>startsAt: ISO 8601 with an explicit offset, e.g. "2026-10-20T08:00:00+07:00". End time is computed by the server.</summary>
public sealed record BookSurveyAppointmentRequest(string? StartsAt, string? Note);

public sealed record CancelSurveyAppointmentRequest(string? Reason);

public sealed record AssignTechnicianRequest(Guid TechnicianUserId, string? Reason = null);

/// <summary>Sales reschedule. <c>technicianUserId</c> is optional: set it to reschedule and reassign in one operation.</summary>
public sealed record SalesRescheduleRequest(string? StartsAt, string? Reason, Guid? TechnicianUserId);

/// <summary>earliestBaseDeparture is a local "HH:mm" override; null/omitted uses the configured default.</summary>
public sealed record UpsertTechnicianProfileRequest(decimal? BaseLatitude, decimal? BaseLongitude, string? BaseLabel, bool IsSchedulingActive, int? MaxAppointmentsPerDay,
    string? EarliestBaseDeparture = null);

public sealed record WorkingPeriodRequest(string? Day, string? Start, string? End);

/// <summary>useDefault = true returns to the configured default; otherwise periods is the complete week (days without periods are days off).</summary>
public sealed record SetWorkingScheduleRequest(bool UseDefault, IReadOnlyList<WorkingPeriodRequest>? Periods);

public sealed record AddUnavailablePeriodRequest(string? StartsAt, string? EndsAt, string? Reason);

/// <summary>
/// Strict ISO 8601 parsing: an explicit offset ("Z" or "+07:00") is required so the instant is never
/// guessed from the server's local time zone. A '+' decoded as a space in a query string is accepted.
/// </summary>
public static partial class IsoTimestamp
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace(' ', '+');
        return Pattern().IsMatch(normalized)
               && DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }
}
