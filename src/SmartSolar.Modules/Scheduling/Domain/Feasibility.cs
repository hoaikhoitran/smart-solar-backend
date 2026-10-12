using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.Domain;

/// <summary>An active appointment on a technician's day.</summary>
public sealed record ScheduledStop(Guid AppointmentId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, GeoPoint? Location);

public sealed record BlockedPeriod(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>A concrete on-site working interval of one technician on one local date, in UTC.</summary>
public sealed record WorkingInterval(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>One interval of a technician's custom weekly schedule (local wall-clock time).</summary>
public sealed record WeeklyPeriod(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>
/// Everything the rules need about one technician for one local day.
/// <para><see cref="CustomWeeklySchedule"/> and <see cref="EarliestBaseDepartureOverride"/> are the raw
/// configuration; <see cref="WorkingIntervals"/> and <see cref="EarliestBaseDepartureUtc"/> are resolved
/// for the date by <see cref="WorkingCalendar.Resolve"/>. Null resolved values mean "unrestricted"
/// (used only by unit tests of other rules).</para>
/// </summary>
public sealed record TechnicianDayState(
    Guid TechnicianUserId,
    string DisplayName,
    GeoPoint? BaseLocation,
    IReadOnlyList<ScheduledStop> Appointments,
    IReadOnlyList<BlockedPeriod> Blocked,
    int? MaxAppointmentsPerDayOverride = null,
    IReadOnlyList<WeeklyPeriod>? CustomWeeklySchedule = null,
    TimeOnly? EarliestBaseDepartureOverride = null,
    IReadOnlyList<WorkingInterval>? WorkingIntervals = null,
    DateTimeOffset? EarliestBaseDepartureUtc = null);

/// <summary>The appointment being placed. <see cref="IgnoreAppointmentId"/> excludes the appointment being moved or reassigned.</summary>
public sealed record AppointmentCandidate(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, GeoPoint Location, Guid? IgnoreAppointmentId = null);

public static class FeasibilityReasons
{
    public const string BaseLocationMissing = "BASE_LOCATION_MISSING";
    public const string AppointmentLocationMissing = "APPOINTMENT_LOCATION_MISSING";
    public const string Unavailable = "TECHNICIAN_UNAVAILABLE_PERIOD";
    public const string DailyLimitReached = "DAILY_LIMIT_REACHED";
    public const string Overlap = "OVERLAPPING_APPOINTMENT";
    public const string CannotArriveInTime = "CANNOT_ARRIVE_IN_TIME";
    public const string LateForNextAppointment = "LATE_FOR_NEXT_APPOINTMENT";
    public const string DepartureInPast = "DEPARTURE_IN_PAST";
    public const string RouteNotVerified = "ROUTE_NOT_VERIFIED";
    public const string OutsideWorkingHours = "OUTSIDE_WORKING_HOURS";
    public const string BeforeEarliestDeparture = "BEFORE_EARLIEST_BASE_DEPARTURE";
}

/// <summary>A route the rules need, with the planned departure time.</summary>
public sealed record RequiredLeg(string Direction, GeoPoint Origin, GeoPoint Destination, DateTimeOffset DepartureAtUtc, string OriginKind, Guid? OriginAppointmentId, Guid? DestinationAppointmentId);

public sealed record LegPlan(
    ScheduledStop? Previous,
    ScheduledStop? Next,
    RequiredLeg Inbound,
    RequiredLeg? Outbound);

public sealed record FeasibilityResult(bool IsFeasible, string? Reason, DateTimeOffset? EarliestArrivalUtc)
{
    public static FeasibilityResult Ok(DateTimeOffset? arrival) => new(true, null, arrival);
    public static FeasibilityResult Fail(string reason, DateTimeOffset? arrival = null) => new(false, reason, arrival);
}

/// <summary>
/// Pure scheduling rules. For a candidate appointment C on a technician's day:
/// <code>
/// PreviousEnd + Travel(Previous → C) + SafetyBuffer &lt;= C.Start      (from the base when there is no previous)
/// C.End      + Travel(C → Next)     + SafetyBuffer &lt;= Next.Start
/// </code>
/// plus: inside one of the technician's working intervals, no overlap, below the daily limit, no
/// unavailable period between departure and end, a first departure from the base no earlier than the
/// allowed time, and every needed route verified. The safety buffer is always added on top of the
/// provider estimate.
/// </summary>
public static class FeasibilityRules
{
    public const string InboundDirection = "INBOUND";
    public const string OutboundDirection = "OUTBOUND";

    /// <summary>Checks that need no travel data. Returns a reason when the technician is already excluded.</summary>
    public static string? PreCheck(TechnicianDayState day, AppointmentCandidate candidate, int maxPerDay)
    {
        if (day.BaseLocation is null) return FeasibilityReasons.BaseLocationMissing;
        if (day.WorkingIntervals is { } intervals
            && !intervals.Any(w => w.StartsAtUtc <= candidate.StartsAtUtc && candidate.EndsAtUtc <= w.EndsAtUtc))
            return FeasibilityReasons.OutsideWorkingHours;

        var others = Others(day, candidate);
        if (others.Count >= maxPerDay) return FeasibilityReasons.DailyLimitReached;
        if (others.Any(a => a.StartsAtUtc < candidate.EndsAtUtc && candidate.StartsAtUtc < a.EndsAtUtc)) return FeasibilityReasons.Overlap;
        if (others.Any(a => a.Location is null)) return FeasibilityReasons.AppointmentLocationMissing;
        if (day.Blocked.Any(b => b.StartsAtUtc < candidate.EndsAtUtc && candidate.StartsAtUtc < b.EndsAtUtc)) return FeasibilityReasons.Unavailable;
        return null;
    }

    /// <summary>
    /// The legs to estimate. Inbound departs at the previous appointment's end (never earlier than
    /// now), or — from the base — at the latest departure that still leaves the safety buffer
    /// (C.Start − buffer); the actual departure is then reported as arrival − travel.
    /// Outbound departs at C's end.
    /// </summary>
    public static LegPlan PlanLegs(TechnicianDayState day, AppointmentCandidate candidate, TimeSpan safetyBuffer, DateTimeOffset nowUtc)
    {
        var others = Others(day, candidate);
        var previous = others.Where(a => a.EndsAtUtc <= candidate.StartsAtUtc).MaxBy(a => a.EndsAtUtc);
        var next = others.Where(a => a.StartsAtUtc >= candidate.EndsAtUtc).MinBy(a => a.StartsAtUtc);

        var inbound = previous is null
            ? new RequiredLeg(InboundDirection, day.BaseLocation!.Value, candidate.Location,
                Max(candidate.StartsAtUtc - safetyBuffer, nowUtc), Constants.TravelOriginKinds.Base, null, null)
            : new RequiredLeg(InboundDirection, previous.Location!.Value, candidate.Location,
                Max(previous.EndsAtUtc, nowUtc), Constants.TravelOriginKinds.Appointment, previous.AppointmentId, null);

        var outbound = next is null
            ? null
            : new RequiredLeg(OutboundDirection, candidate.Location, next.Location!.Value, candidate.EndsAtUtc,
                Constants.TravelOriginKinds.Appointment, null, next.AppointmentId);

        return new LegPlan(previous, next, inbound, outbound);
    }

    public static FeasibilityResult Evaluate(
        TechnicianDayState day,
        AppointmentCandidate candidate,
        LegPlan plan,
        TravelEstimateResult inbound,
        TravelEstimateResult? outbound,
        TimeSpan safetyBuffer,
        DateTimeOffset nowUtc)
    {
        if (!inbound.IsOk) return FeasibilityResult.Fail(FeasibilityReasons.RouteNotVerified);

        DateTimeOffset arrival;
        DateTimeOffset departure;
        if (plan.Previous is null)
        {
            // From the base: leave early enough to arrive with the buffer, but not in the past.
            departure = candidate.StartsAtUtc - safetyBuffer - inbound.Duration!.Value;
            arrival = candidate.StartsAtUtc - safetyBuffer;
            if (departure < nowUtc) return FeasibilityResult.Fail(FeasibilityReasons.DepartureInPast, nowUtc + inbound.Duration.Value + safetyBuffer);

            // RequiredDeparture >= EarliestAllowedBaseDeparture. This is a travel rule, separate from
            // on-site working hours: leaving before the working day starts is fine only down to this limit.
            if (day.EarliestBaseDepartureUtc is { } earliest && departure < earliest)
                return FeasibilityResult.Fail(FeasibilityReasons.BeforeEarliestDeparture, earliest + inbound.Duration.Value + safetyBuffer);
        }
        else
        {
            departure = Max(plan.Previous.EndsAtUtc, nowUtc);
            arrival = departure + inbound.Duration!.Value;
            if (arrival + safetyBuffer > candidate.StartsAtUtc)
                return FeasibilityResult.Fail(FeasibilityReasons.CannotArriveInTime, arrival + safetyBuffer);
        }

        if (day.Blocked.Any(b => b.StartsAtUtc < candidate.EndsAtUtc && departure < b.EndsAtUtc))
            return FeasibilityResult.Fail(FeasibilityReasons.Unavailable, arrival);

        if (plan.Next is not null)
        {
            if (outbound is null || !outbound.IsOk) return FeasibilityResult.Fail(FeasibilityReasons.RouteNotVerified, arrival);
            if (candidate.EndsAtUtc + outbound.Duration!.Value + safetyBuffer > plan.Next.StartsAtUtc)
                return FeasibilityResult.Fail(FeasibilityReasons.LateForNextAppointment, arrival);
        }

        return FeasibilityResult.Ok(arrival);
    }

    private static List<ScheduledStop> Others(TechnicianDayState day, AppointmentCandidate candidate)
        => day.Appointments.Where(a => a.AppointmentId != candidate.IgnoreAppointmentId).ToList();

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
