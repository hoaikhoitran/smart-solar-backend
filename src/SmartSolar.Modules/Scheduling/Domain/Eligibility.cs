namespace SmartSolar.Modules.Scheduling.Domain;

/// <summary>
/// A commitment on a technician's day: a survey appointment (a pending capacity hold or a confirmed
/// job) or, with Installation Flow enabled, an installation assignment. Every commitment blocks
/// overlapping work including the buffer; only survey appointments count toward the per-day limit.
/// </summary>
public sealed record ScheduledStop(Guid AppointmentId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, bool CountsTowardDailyLimit = true, bool IsPendingHold = false);

/// <summary>Leave or other time the technician cannot work.</summary>
public sealed record BlockedPeriod(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>A concrete on-site working interval of one technician on one local date, in UTC.</summary>
public sealed record WorkingInterval(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>One interval of a technician's custom weekly schedule (local wall-clock time).</summary>
public sealed record WeeklyPeriod(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>
/// Everything the eligibility rules need about one technician for one local day.
/// <see cref="CustomWeeklySchedule"/> is raw configuration; <see cref="WorkingIntervals"/> is resolved for
/// the date by <see cref="WorkingCalendar.Resolve"/>. Null <see cref="WorkingIntervals"/> or
/// <see cref="ZoneIds"/> mean "unrestricted" (only used by unit tests of other rules).
/// </summary>
public sealed record TechnicianDayState(
    Guid TechnicianUserId,
    string DisplayName,
    IReadOnlyList<ScheduledStop> Appointments,
    IReadOnlyList<BlockedPeriod> Blocked,
    int? MaxAppointmentsPerDayOverride = null,
    IReadOnlyList<WeeklyPeriod>? CustomWeeklySchedule = null,
    IReadOnlyList<WorkingInterval>? WorkingIntervals = null,
    bool IsActiveAccount = true,
    bool HasTechnicianRole = true,
    bool IsSchedulingActive = true,
    IReadOnlySet<Guid>? ZoneIds = null)
{
    /// <summary>Survey commitments that count toward the daily limit (pending holds included).</summary>
    public int CountedCommitments(IReadOnlySet<Guid>? ignore = null)
        => Appointments.Count(a => a.CountsTowardDailyLimit && (ignore is null || !ignore.Contains(a.AppointmentId)));

    public TechnicianDayState With(ScheduledStop stop) => this with { Appointments = Appointments.Append(stop).OrderBy(a => a.StartsAtUtc).ToList() };

    public TechnicianDayState Without(Guid appointmentId) => this with { Appointments = Appointments.Where(a => a.AppointmentId != appointmentId).ToList() };
}

/// <summary>Stable reason codes for an ineligible technician (also used as API error codes).</summary>
public static class EligibilityReasons
{
    public const string TechnicianInactive = "TECHNICIAN_INACTIVE";
    public const string SchedulingDisabled = "TECHNICIAN_SCHEDULING_DISABLED";
    /// <summary>None of the technician's active zones is the zone of the site. Replaces TECHNICIAN_OUTSIDE_SERVICE_AREA.</summary>
    public const string OutsideZone = "TECHNICIAN_OUTSIDE_ZONE";
    public const string OutsideWorkingHours = "OUTSIDE_WORKING_HOURS";
    public const string OnLeave = "TECHNICIAN_ON_LEAVE";
    public const string ScheduleConflict = "TECHNICIAN_SCHEDULE_CONFLICT";
    public const string DailyLimitReached = "DAILY_LIMIT_REACHED";

    public static readonly IReadOnlyList<string> All =
        [TechnicianInactive, SchedulingDisabled, OutsideZone, OutsideWorkingHours, OnLeave, ScheduleConflict, DailyLimitReached];
}

/// <summary>
/// The work interval being checked. <see cref="AcceptableZoneIds"/> is the active zone of the site's commune
/// (null = no zone restriction; empty = nobody qualifies).
/// <see cref="IgnoreCommitmentIds"/> excludes the appointment being moved or the commitments being replaced.
/// </summary>
public sealed record EligibilityRequest(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyCollection<Guid>? AcceptableZoneIds,
    IReadOnlySet<Guid>? IgnoreCommitmentIds = null,
    bool CountsTowardDailyLimit = true);

/// <summary>
/// Pure eligibility rules shared by survey scheduling and installation planning. A technician may take
/// an interval [S, E) when, in this order: the account is active and has the TECHNICIAN role; scheduling
/// is enabled; they are an active member of the site's zone; [S, E) lies inside one working
/// interval; no leave overlaps it; every other commitment C satisfies
/// <c>C.End + buffer &lt;= S</c> or <c>E + buffer &lt;= C.Start</c> (a conservative fixed buffer between
/// jobs, not a travel-time estimate); and the daily survey limit is not reached.
/// </summary>
public static class EligibilityRules
{
    public static string? Check(TechnicianDayState day, EligibilityRequest request, int defaultMaxPerDay, TimeSpan buffer)
    {
        if (!day.IsActiveAccount || !day.HasTechnicianRole) return EligibilityReasons.TechnicianInactive;
        if (!day.IsSchedulingActive) return EligibilityReasons.SchedulingDisabled;
        if (request.AcceptableZoneIds is { } zones && day.ZoneIds is { } memberships && !zones.Any(memberships.Contains))
            return EligibilityReasons.OutsideZone;
        if (day.WorkingIntervals is { } intervals
            && !intervals.Any(w => w.StartsAtUtc <= request.StartsAtUtc && request.EndsAtUtc <= w.EndsAtUtc))
            return EligibilityReasons.OutsideWorkingHours;
        if (day.Blocked.Any(b => b.StartsAtUtc < request.EndsAtUtc && request.StartsAtUtc < b.EndsAtUtc))
            return EligibilityReasons.OnLeave;

        var others = day.Appointments.Where(a => request.IgnoreCommitmentIds is null || !request.IgnoreCommitmentIds.Contains(a.AppointmentId)).ToList();
        if (others.Any(a => Conflicts(a, request.StartsAtUtc, request.EndsAtUtc, buffer))) return EligibilityReasons.ScheduleConflict;
        if (request.CountsTowardDailyLimit && others.Count(a => a.CountsTowardDailyLimit) >= (day.MaxAppointmentsPerDayOverride ?? defaultMaxPerDay))
            return EligibilityReasons.DailyLimitReached;
        return null;
    }

    /// <summary>
    /// A possibly multi-day work interval [S, E) of installation work. <paramref name="days"/> are the technician's
    /// resolved states for every local date the interval touches, in date order. Same account, scheduling and zone
    /// rules as <see cref="Check"/>; then every touched date must be a working day; S must fall inside the first day's
    /// working envelope (first period start to last period end, breaks included) and E inside the last day's; leave
    /// overlapping the working part of any day blocks; and every other commitment must keep the buffer from [S, E)
    /// (the technician is committed for the whole interval). The survey daily limit does not apply.
    /// </summary>
    public static string? CheckSpan(IReadOnlyList<TechnicianDayState> days, EligibilityRequest request, TimeSpan buffer)
    {
        if (days.Count == 0) return EligibilityReasons.TechnicianInactive;
        var first = days[0];
        if (!first.IsActiveAccount || !first.HasTechnicianRole) return EligibilityReasons.TechnicianInactive;
        if (!first.IsSchedulingActive) return EligibilityReasons.SchedulingDisabled;
        if (request.AcceptableZoneIds is { } zones && first.ZoneIds is { } memberships && !zones.Any(memberships.Contains))
            return EligibilityReasons.OutsideZone;

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            if (day.WorkingIntervals is not { } intervals) continue;
            if (intervals.Count == 0) return EligibilityReasons.OutsideWorkingHours;
            var envelopeStart = intervals.Min(w => w.StartsAtUtc);
            var envelopeEnd = intervals.Max(w => w.EndsAtUtc);
            if (i == 0 && (request.StartsAtUtc < envelopeStart || request.StartsAtUtc >= envelopeEnd)) return EligibilityReasons.OutsideWorkingHours;
            if (i == days.Count - 1 && (request.EndsAtUtc > envelopeEnd || request.EndsAtUtc <= envelopeStart)) return EligibilityReasons.OutsideWorkingHours;
            var partStart = i == 0 ? request.StartsAtUtc : envelopeStart;
            var partEnd = i == days.Count - 1 ? request.EndsAtUtc : envelopeEnd;
            if (day.Blocked.Any(b => b.StartsAtUtc < partEnd && partStart < b.EndsAtUtc)) return EligibilityReasons.OnLeave;
        }

        var others = days.SelectMany(d => d.Appointments).DistinctBy(a => a.AppointmentId)
            .Where(a => request.IgnoreCommitmentIds is null || !request.IgnoreCommitmentIds.Contains(a.AppointmentId));
        return others.Any(a => Conflicts(a, request.StartsAtUtc, request.EndsAtUtc, buffer)) ? EligibilityReasons.ScheduleConflict : null;
    }

    /// <summary>True when the commitment and [start, end) are closer than the buffer (or overlap).</summary>
    public static bool Conflicts(ScheduledStop stop, DateTimeOffset start, DateTimeOffset end, TimeSpan buffer)
        => stop.StartsAtUtc < end + buffer && start < stop.EndsAtUtc + buffer;
}
