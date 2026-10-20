using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Installations;

/// <summary>
/// Checks a responsible-technician interval with the shared <see cref="TechnicianEligibility"/>: active account,
/// TECHNICIAN role, scheduling enabled, membership in one of the active service areas covering the site, every
/// touched local date a working day (start/end inside that day's working hours), no leave on the working part of
/// any day, and no other commitment — confirmed survey appointments and other current installation assignments —
/// within <c>Scheduling:BufferBetweenAppointmentsMinutes</c>. The technician's own PENDING_ACCEPTANCE/ESCALATED survey
/// capacity holds do not make them unavailable: the plan lists the holds overlapping the interval (buffer included) so the
/// caller re-homes them to other eligible technicians in the same transaction (<see cref="Rehoming"/>), or refuses with
/// TECHNICIAN_SCHEDULE_CONFLICT when one cannot be handed over. The project's own current assignment is ignored (it is
/// being replaced). No transaction is opened here; callers re-check under the technician locks.
/// </summary>
public sealed class InstallationSchedulePlanner(TechnicianEligibility eligibility, WorkingCalendar calendar, InstallationOptions options,
    AppointmentAssignmentService rehoming)
{
    public WorkingCalendar Calendar => calendar;

    /// <summary>The shared survey-hold re-home planner (detach/attach inside the caller's transaction).</summary>
    public AppointmentAssignmentService Rehoming => rehoming;

    /// <summary>
    /// Null when the interval is acceptable; otherwise a human-readable reason (error INSTALLATION_INVALID_SCHEDULE). The longest
    /// assignment is <c>installation.maxAssignmentDays</c> of the current settings; the planning horizon stays a configuration value.
    /// </summary>
    public async Task<string?> ValidateIntervalAsync(DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var maxDays = (await eligibility.PolicyAsync(cancellationToken)).InstallationMaxAssignmentDays;
        if (endsAt <= startsAt) return "The scheduled end must be after the start.";
        if (startsAt <= now) return "The scheduled start must be in the future.";
        if (calendar.LocalDateOf(startsAt) > calendar.LocalDateOf(now).AddDays(options.MaxPlanningHorizonDays))
            return $"The installation must start within {options.MaxPlanningHorizonDays} days.";
        var days = calendar.LocalDateOf(endsAt.AddTicks(-1)).DayNumber - calendar.LocalDateOf(startsAt).DayNumber + 1;
        if (days > maxDays) return $"An assignment may span at most {maxDays} local days.";
        return null;
    }

    /// <summary>The technician's name, the reason they cannot take the interval (null = eligible) and, when eligible, the holds to re-home.</summary>
    public async Task<(string Name, string? Reason, HoldRehomePlan? Plan)> CheckAsync(Guid technicianUserId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        IReadOnlyCollection<Guid>? zoneIds, IReadOnlySet<Guid>? ignoreAssignmentIds, CancellationToken cancellationToken)
    {
        var (name, strict) = (await eligibility.CheckSpanAsync([technicianUserId], Request(startsAt, endsAt, zoneIds, ignoreAssignmentIds), cancellationToken))[technicianUserId];
        if (strict is not null && strict != EligibilityReasons.ScheduleConflict) return (name, strict, null);
        var (plan, reason) = await rehoming.PlanSpanAsync(technicianUserId, startsAt.ToUniversalTime(), endsAt.ToUniversalTime(), zoneIds, ignoreAssignmentIds,
            cancellationToken);
        return (name, reason, plan);
    }

    /// <summary>Candidates with their reason; a technician blocked only by their own pending survey holds is listed as eligible (the holds are re-homed on assignment).</summary>
    /// <param name="zoneIds">The site's zone; null = cross-zone candidates (the zone-membership rule is skipped, every other rule applies).</param>
    public async Task<IReadOnlyList<InstallationCandidateView>> CandidatesAsync(IReadOnlyCollection<Guid> technicianUserIds, DateTimeOffset startsAt, DateTimeOffset endsAt,
        IReadOnlyCollection<Guid>? zoneIds, IReadOnlySet<Guid>? ignoreAssignmentIds, CancellationToken cancellationToken)
    {
        var views = new List<InstallationCandidateView>();
        foreach (var (id, (name, strict)) in await eligibility.CheckSpanAsync(technicianUserIds, Request(startsAt, endsAt, zoneIds, ignoreAssignmentIds), cancellationToken))
        {
            var reason = strict == EligibilityReasons.ScheduleConflict
                ? (await rehoming.PlanSpanAsync(id, startsAt.ToUniversalTime(), endsAt.ToUniversalTime(), zoneIds, ignoreAssignmentIds, cancellationToken)).Reason
                : strict;
            views.Add(new InstallationCandidateView(id, name, reason is null, reason, CrossZone: zoneIds is null));
        }
        return views.OrderByDescending(c => c.IsEligible).ThenBy(c => c.FullName, StringComparer.Ordinal).ThenBy(c => c.TechnicianUserId).ToList();
    }

    private static EligibilityRequest Request(DateTimeOffset startsAt, DateTimeOffset endsAt, IReadOnlyCollection<Guid>? zones, IReadOnlySet<Guid>? ignore)
        => new(startsAt.ToUniversalTime(), endsAt.ToUniversalTime(), zones, ignore, CountsTowardDailyLimit: false);
}
