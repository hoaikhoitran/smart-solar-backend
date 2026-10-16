using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Installations;

public sealed record ShiftInput(DateOnly Date, TimeOnly Start, TimeOnly End);

public sealed record PlannedShift(Guid Key, DateOnly Date, TimeOnly LocalStart, TimeOnly LocalEnd, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

public sealed record ScheduleConflict(Guid TechnicianUserId, string TechnicianName, DateOnly Date, string Start, string End, string Reason, string? RouteStatus);

/// <summary>Verified legs for one technician × shift, re-checked under the lock before commit.</summary>
public sealed record VerifiedPlacement(Guid TechnicianUserId, PlannedShift Shift, LegPlan Plan, TravelEstimateResult Inbound, TravelEstimateResult? Outbound,
    DateTimeOffset? EarliestArrivalUtc);

public sealed record SchedulePlan(IReadOnlyList<PlannedShift> Shifts, IReadOnlyList<VerifiedPlacement> Placements, IReadOnlyList<ScheduleConflict> Conflicts)
{
    public bool IsFeasible => Conflicts.Count == 0;
}

/// <summary>
/// Checks a team's shared shifts against each technician's real day using the Flow 1 scheduling
/// core: working schedule, leave, earliest base departure, survey appointments and other
/// installation shifts, and Vietmap motorcycle travel + safety buffer to the previous and next
/// commitment. The project's own future assignments are ignored (they are being replaced) and the
/// other planned shifts of the same day are treated as commitments, so a full day plan is
/// validated as a whole. Installation shifts never count toward the survey-per-day limit.
/// No transaction is open here; the caller re-checks under the technician locks.
/// </summary>
public sealed class InstallationSchedulePlanner(ISchedulingUnitOfWork scheduling, ITravelTimeEstimator estimator, WorkingCalendar calendar, SchedulingOptions options, TimeProvider time)
{
    public TimeSpan SafetyBuffer => TimeSpan.FromMinutes(options.SafetyBufferMinutes);

    public IReadOnlyList<PlannedShift> ToPlannedShifts(IReadOnlyList<ShiftInput> shifts)
        => shifts.OrderBy(s => s.Date).ThenBy(s => s.Start)
            .Select(s => new PlannedShift(Guid.NewGuid(), s.Date, s.Start, s.End, calendar.ToUtc(s.Date, s.Start), calendar.ToUtc(s.Date, s.End)))
            .ToList();

    public async Task<SchedulePlan> EvaluateAsync(IReadOnlyList<Guid> technicianIds, IReadOnlyList<PlannedShift> shifts, GeoPoint site,
        IReadOnlySet<Guid> ownAssignmentIds, CancellationToken cancellationToken)
    {
        var budget = new RouteBudget(estimator, Math.Max(options.MaxRouteRequestsPerOperation, technicianIds.Count * shifts.Count * 2));
        var placements = new List<VerifiedPlacement>();
        var conflicts = new List<ScheduleConflict>();
        var loader = new TechnicianDayLoader(scheduling, calendar);

        foreach (var date in shifts.Select(s => s.Date).Distinct())
        {
            var dayShifts = shifts.Where(s => s.Date == date).ToList();
            var days = await loader.LoadAsync(date, null, cancellationToken);
            foreach (var technicianId in technicianIds)
            {
                var day = days.FirstOrDefault(d => d.TechnicianUserId == technicianId);
                if (day is null)
                {
                    conflicts.AddRange(dayShifts.Select(s => Conflict(technicianId, "?", s, "TECHNICIAN_NOT_SCHEDULABLE", null)));
                    continue;
                }
                foreach (var shift in dayShifts)
                {
                    var (placement, conflict) = await PlaceAsync(WithPlan(day, dayShifts, shift, site, ownAssignmentIds), shift, site, budget, cancellationToken);
                    if (placement is not null) placements.Add(placement);
                    if (conflict is not null) conflicts.Add(conflict);
                }
            }
        }
        return new SchedulePlan(shifts, placements, conflicts);
    }

    /// <summary>Under the lock: same neighbours and every rule still satisfied with the verified legs.</summary>
    public async Task<bool> RecheckAsync(SchedulePlan plan, GeoPoint site, IReadOnlySet<Guid> ownAssignmentIds, CancellationToken cancellationToken)
    {
        var loader = new TechnicianDayLoader(scheduling, calendar);
        var now = time.GetUtcNow();
        foreach (var group in plan.Placements.GroupBy(p => (p.TechnicianUserId, p.Shift.Date)))
        {
            var fresh = (await loader.LoadAsync(group.Key.Date, group.Key.TechnicianUserId, cancellationToken)).SingleOrDefault();
            if (fresh is null) return false;
            var dayShifts = plan.Shifts.Where(s => s.Date == group.Key.Date).ToList();
            foreach (var placement in group)
            {
                var day = WithPlan(fresh, dayShifts, placement.Shift, site, ownAssignmentIds);
                var candidate = Candidate(placement.Shift, site);
                if (FeasibilityRules.PreCheck(day, candidate, int.MaxValue) is not null) return false;
                var legs = FeasibilityRules.PlanLegs(day, candidate, SafetyBuffer, now);
                if (!Same(legs.Previous, placement.Plan.Previous) || !Same(legs.Next, placement.Plan.Next)) return false;
                if (!FeasibilityRules.Evaluate(day, candidate, placement.Plan, placement.Inbound, placement.Outbound, SafetyBuffer, now).IsFeasible) return false;
            }
        }
        return true;
    }

    private async Task<(VerifiedPlacement?, ScheduleConflict?)> PlaceAsync(TechnicianDayState day, PlannedShift shift, GeoPoint site, RouteBudget budget, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var candidate = Candidate(shift, site);
        var reason = FeasibilityRules.PreCheck(day, candidate, int.MaxValue);
        if (reason is not null) return (null, Conflict(day.TechnicianUserId, day.DisplayName, shift, reason, null));

        var legs = FeasibilityRules.PlanLegs(day, candidate, SafetyBuffer, now);
        var inbound = await budget.EstimateAsync(new TravelEstimateRequest(legs.Inbound.Origin, legs.Inbound.Destination, legs.Inbound.DepartureAtUtc), cancellationToken);
        if (!inbound.IsOk) return (null, Conflict(day.TechnicianUserId, day.DisplayName, shift, FeasibilityReasons.RouteNotVerified, inbound.Status));
        TravelEstimateResult? outbound = null;
        if (legs.Outbound is { } leg)
        {
            outbound = await budget.EstimateAsync(new TravelEstimateRequest(leg.Origin, leg.Destination, leg.DepartureAtUtc), cancellationToken);
            if (!outbound.IsOk) return (null, Conflict(day.TechnicianUserId, day.DisplayName, shift, FeasibilityReasons.RouteNotVerified, outbound.Status));
        }
        var result = FeasibilityRules.Evaluate(day, candidate, legs, inbound, outbound, SafetyBuffer, now);
        return result.IsFeasible
            ? (new VerifiedPlacement(day.TechnicianUserId, shift, legs, inbound, outbound, result.EarliestArrivalUtc), null)
            : (null, Conflict(day.TechnicianUserId, day.DisplayName, shift, result.Reason!, null));
    }

    /// <summary>The day without this project's current assignments, plus the other planned shifts of the same date.</summary>
    private static TechnicianDayState WithPlan(TechnicianDayState day, IReadOnlyList<PlannedShift> dayShifts, PlannedShift current, GeoPoint site, IReadOnlySet<Guid> ownAssignmentIds)
        => day with
        {
            Appointments = day.Appointments.Where(a => !ownAssignmentIds.Contains(a.AppointmentId))
                .Concat(dayShifts.Where(s => s.Key != current.Key).Select(s => new ScheduledStop(s.Key, s.StartsAtUtc, s.EndsAtUtc, site, CountsTowardDailyLimit: false)))
                .OrderBy(a => a.StartsAtUtc).ToList(),
        };

    private static AppointmentCandidate Candidate(PlannedShift shift, GeoPoint site) => new(shift.StartsAtUtc, shift.EndsAtUtc, site);

    private static bool Same(ScheduledStop? a, ScheduledStop? b)
        => a is null ? b is null : b is not null && a.AppointmentId == b.AppointmentId && a.StartsAtUtc == b.StartsAtUtc && a.EndsAtUtc == b.EndsAtUtc && a.Location == b.Location;

    private static ScheduleConflict Conflict(Guid technicianId, string name, PlannedShift shift, string reason, string? routeStatus)
        => new(technicianId, name, shift.Date, shift.LocalStart.ToString("HH:mm"), shift.LocalEnd.ToString("HH:mm"), reason, routeStatus);
}
