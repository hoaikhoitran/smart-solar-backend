namespace SmartSolar.Modules.Scheduling.Domain;

/// <summary>
/// A commitment on a technician's day: a survey appointment (a pending capacity hold or a confirmed
/// job) or, with Installation Flow enabled, an installation assignment. Every commitment blocks
/// overlapping work including the buffer; only survey appointments count toward the per-day limit.
/// </summary>
/// <param name="InExecution">The work is being performed now (survey report IN_PROGRESS; installation IN_PROGRESS, or rework with current-round
/// evidence) and has run past its reserved end (overtime): the unit of work sets <see cref="EndsAtUtc"/> to the current time and the day loader extends
/// it to the end of that local day (<see cref="EffectiveBusy.OvertimeHoldUntil"/>). False for every other stop.</param>
/// <param name="IsInstallation">An installation (or after-sales) assignment rather than a survey appointment.</param>
/// <param name="ReservedStartsAtUtc">The reserved (historical) interval when it differs from the effective one; null = same as the effective interval.
/// The daily survey limit is charged to the local day of the reserved start (a survey completed a day early still counts on its scheduled day).</param>
public sealed record ScheduledStop(Guid AppointmentId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, bool CountsTowardDailyLimit = true, bool IsPendingHold = false,
    bool InExecution = false, bool IsInstallation = false, DateTimeOffset? ReservedStartsAtUtc = null, DateTimeOffset? ReservedEndsAtUtc = null)
{
    public DateTimeOffset ReservedStart => ReservedStartsAtUtc ?? StartsAtUtc;
    public DateTimeOffset ReservedEnd => ReservedEndsAtUtc ?? EndsAtUtc;
}

/// <summary>
/// Effective busy interval of a commitment for FUTURE scheduling (2026-11-25). The reserved interval [startsAt, endsAt) stays the historical
/// schedule. Once the work is finished (a survey appointment COMPLETED, an installation assignment whose completion was reported) the busy interval
/// ends at the confirmed finish time: early → the unused rest is released (zero-length point [finishedAt, finishedAt) when it finished before the
/// reserved start); late (overtime) → it lasts until the real finish, so the buffer is kept after the real end. The configured buffer is applied
/// around this interval by <see cref="EligibilityRules.Conflicts"/> (never added here).
/// <para>While the work is still being performed past its reserved end (see <see cref="ScheduledStop.InExecution"/>) it keeps occupying the technician
/// until the end of that local day (<see cref="OvertimeHoldUntil"/>), re-evaluated at every decision.</para>
/// <para>The PostgreSQL exclusion constraints are a backstop that is never stricter than this rule: they use the reserved end cut at an early finish
/// (an empty range when the work finished before the reserved start) and ignore overtime, which only ever concerns time already in the past
/// for them (a time-dependent "now" cannot be part of an index).</para>
/// </summary>
public static class EffectiveBusy
{
    public static (DateTimeOffset StartsAt, DateTimeOffset EndsAt) Of(DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset? finishedAt)
    {
        if (finishedAt is not { } finished) return (startsAt, endsAt);
        return finished <= startsAt ? (finished, finished) : (startsAt, finished);
    }

    /// <summary>Overtime hold: an overrunning job occupies its technician until the end of the local day in which it is observed running.</summary>
    public static DateTimeOffset OvertimeHoldUntil(DateTimeOffset observedAtUtc, WorkingCalendar calendar)
        => calendar.DayBoundsUtc(calendar.LocalDateOf(observedAtUtc)).EndUtc;
}

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
