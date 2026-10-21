using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartSolar.Api.Contracts.Scheduling;

/// <summary>startsAt: ISO 8601 with an explicit offset, e.g. "2026-10-20T08:00:00+07:00". End time is computed by the server.</summary>
public sealed record BookSurveyAppointmentRequest(string? StartsAt, string? Note);

public sealed record CancelSurveyAppointmentRequest(string? Reason);

/// <summary>Staff assignment: reason is required (at most 500 characters); expectedRevision guards against acting on a stale view.</summary>
public sealed record AssignTechnicianRequest(Guid TechnicianUserId, string? Reason = null, int? ExpectedRevision = null);

/// <summary>Sales reschedule. <c>technicianUserId</c> is optional: set it (with a reason) to reschedule and assign in one operation.</summary>
public sealed record SalesRescheduleRequest(string? StartsAt, string? Reason, Guid? TechnicianUserId);

public sealed record UpsertTechnicianProfileRequest(bool IsSchedulingActive, int? MaxAppointmentsPerDay);

/// <summary>The technician's complete zone set (a technician may belong to several zones); [] removes every membership.</summary>
public sealed record SetTechnicianZonesRequest(IReadOnlyList<Guid>? ZoneIds);

public sealed record AcceptAppointmentRequest(int? ExpectedRevision);

public sealed record ReleaseAppointmentRequest(string? Reason);

public sealed record SetSalesOwnerRequest(Guid SaleUserId, string? Reason);

/// <summary>code: internal zone code (A–Z, 0–9, '-', 2–50 characters; upper-cased by the server), immutable once created.</summary>
public sealed record CreateZoneRequest(string? Code, string? Name, bool IsActive = true, bool IsServiceable = true, Guid? ResponsibleManagerUserId = null);

/// <summary>isActive / isServiceable are optional: omitted (null) keeps the current flag (a rename never deactivates the zone).</summary>
public sealed record UpdateZoneRequest(string? Name, bool? IsActive, bool? IsServiceable, Guid? ResponsibleManagerUserId, int Revision);

/// <summary>The zone's complete commune set (official five-digit codes); communes left out are removed with <c>reason</c>.</summary>
public sealed record SetZoneCommunesRequest(IReadOnlyList<string>? CommuneCodes, string? Reason, int Revision);

/// <summary>Official location of a site: two-digit province code and five-digit current commune code (both required, leading zeros kept).</summary>
public sealed record SetSiteAdministrativeLocationRequest(string? ProvinceCode, string? CommuneCode);

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
