using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Scheduling.Zones;

/// <summary>
/// A zone member as seen by a coverage evaluation. <see cref="Reason"/> is null for an eligible member, otherwise an eligibility
/// reason code (TECHNICIAN_INACTIVE, TECHNICIAN_SCHEDULING_DISABLED, OUTSIDE_WORKING_HOURS, TECHNICIAN_ON_LEAVE,
/// TECHNICIAN_SCHEDULE_CONFLICT, DAILY_LIMIT_REACHED) or, without a requested interval, NO_AVAILABLE_TECHNICIAN (no bookable slot
/// in the booking horizon). <see cref="RequiresHoldRehoming"/>: eligible only once their own pending capacity holds are re-homed
/// (assignment does that automatically).
/// </summary>
public sealed record CoverageMember(Guid TechnicianUserId, string FullName, bool IsSchedulable, string? Reason, bool RequiresHoldRehoming = false);

/// <summary>
/// Result of <see cref="ZoneCoverageService.EvaluateAsync"/>: one stable <see cref="Status"/> from <see cref="CoverageStatuses"/>,
/// the site location and zone (<see cref="Site"/>), and the zone members split into eligible / ineligible with reasons. Members are
/// only evaluated when the site is in an active, serviceable zone.
/// </summary>
public sealed record CoverageEvaluation(
    Guid PropertySiteId,
    string Status,
    SiteCoverage Site,
    IReadOnlyList<CoverageMember> Eligible,
    IReadOnlyList<CoverageMember> Ineligible,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc)
{
    public bool IsCovered => Status == CoverageStatuses.Covered;
}

/// <summary>
/// The single coverage evaluation shared by Flow 1, staff tools and (later) uncovered-zone dispatch. For a site and an optional
/// requested interval it returns, in this order of precedence:
/// <list type="number">
/// <item>SITE_LOCATION_REQUIRED — no valid (official, in force, same-province) commune on the site;</item>
/// <item>NO_ZONE_COVERAGE — the commune is not mapped to an ACTIVE zone;</item>
/// <item>ZONE_NOT_SERVICEABLE — the zone is active but is_serviceable = false;</item>
/// <item>NO_ZONE_TECHNICIAN — no active membership of a schedulable technician (active account, TECHNICIAN role, scheduling on);</item>
/// <item>with an interval: COVERED when a member is eligible for it (<see cref="TechnicianEligibility"/>, own pending holds re-homable);
/// otherwise SCHEDULE_CONFLICT when some member can still take a bookable slot in the booking horizon, else NO_AVAILABLE_TECHNICIAN;</item>
/// <item>without an interval: COVERED when some member can take a bookable slot in the booking horizon, else NO_AVAILABLE_TECHNICIAN.</item>
/// </list>
/// Read-only (no locks): a booking or assignment re-checks everything under its locks.
/// </summary>
public sealed class ZoneCoverageService(ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, TimeProvider time)
{
    public const string NoAvailabilityReason = CoverageStatuses.NoAvailableTechnician;

    public async Task<CoverageEvaluation> EvaluateAsync(Guid propertySiteId, DateTimeOffset? startsAtUtc = null, DateTimeOffset? endsAtUtc = null,
        IReadOnlySet<Guid>? ignoreCommitmentIds = null, CancellationToken cancellationToken = default)
    {
        if (startsAtUtc is null != endsAtUtc is null) throw new ArgumentException("Give both ends of the interval or neither.");
        if (startsAtUtc is { } s && endsAtUtc is { } e && e <= s) throw new ArgumentException("The interval must end after it starts.");
        var start = startsAtUtc?.ToUniversalTime();
        var end = endsAtUtc?.ToUniversalTime();

        var site = await unitOfWork.GetSiteCoverageAsync(propertySiteId, cancellationToken);
        if (site.Status != CoverageStatuses.Covered) return Result(site.Status, [], []);

        var zoneIds = site.ZoneIds;
        var memberIds = await unitOfWork.ListZoneMemberIdsAsync(zoneIds, cancellationToken);
        if (memberIds.Count == 0) return Result(CoverageStatuses.NoZoneTechnician, [], []);

        var referenceDate = eligibility.Calendar.LocalDateOf(start ?? time.GetUtcNow());
        var states = await eligibility.LoadDaysAsync(referenceDate, new TechnicianScope(memberIds, SchedulableOnly: false), cancellationToken);
        var unschedulable = states.Where(d => !Schedulable(d))
            .Select(d => new CoverageMember(d.TechnicianUserId, d.DisplayName, false, eligibility.Evaluate(d, Probe(d, zoneIds)) ?? EligibilityReasons.TechnicianInactive))
            .ToList();
        var schedulable = states.Where(Schedulable).ToList();
        if (schedulable.Count == 0) return Result(CoverageStatuses.NoZoneTechnician, [], Sort(unschedulable));

        var available = await AvailableInHorizonAsync(schedulable.Select(d => d.TechnicianUserId).ToList(), zoneIds, cancellationToken);
        if (start is null)
        {
            var eligibleNow = schedulable.Where(d => available.Contains(d.TechnicianUserId))
                .Select(d => new CoverageMember(d.TechnicianUserId, d.DisplayName, true, null)).ToList();
            var idle = schedulable.Where(d => !available.Contains(d.TechnicianUserId))
                .Select(d => new CoverageMember(d.TechnicianUserId, d.DisplayName, true, NoAvailabilityReason));
            return Result(eligibleNow.Count > 0 ? CoverageStatuses.Covered : CoverageStatuses.NoAvailableTechnician, Sort(eligibleNow), Sort(idle.Concat(unschedulable)));
        }

        var eligible = new List<CoverageMember>();
        var ineligible = new List<CoverageMember>(unschedulable);
        var singleDay = eligibility.Calendar.LocalDateOf(start.Value) == eligibility.Calendar.LocalDateOf(end!.Value.AddTicks(-1));
        if (singleDay)
        {
            foreach (var day in schedulable)
            {
                var holds = day.Appointments.Where(a => a.IsPendingHold).Select(a => a.AppointmentId);
                var lenient = new EligibilityRequest(start.Value, end.Value, zoneIds, holds.Concat(ignoreCommitmentIds ?? new HashSet<Guid>()).ToHashSet());
                var reason = eligibility.Evaluate(day, lenient);
                if (reason is not null)
                {
                    ineligible.Add(new CoverageMember(day.TechnicianUserId, day.DisplayName, true, reason));
                    continue;
                }
                var strict = eligibility.Evaluate(day, new EligibilityRequest(start.Value, end.Value, zoneIds, ignoreCommitmentIds));
                eligible.Add(new CoverageMember(day.TechnicianUserId, day.DisplayName, true, null, strict is not null));
            }
        }
        else
        {
            var spans = await eligibility.CheckSpanAsync(schedulable.Select(d => d.TechnicianUserId).ToList(),
                new EligibilityRequest(start.Value, end.Value, zoneIds, ignoreCommitmentIds, CountsTowardDailyLimit: false), cancellationToken);
            foreach (var day in schedulable)
            {
                var reason = spans.TryGetValue(day.TechnicianUserId, out var r) ? r.Reason : EligibilityReasons.TechnicianInactive;
                if (reason is null) eligible.Add(new CoverageMember(day.TechnicianUserId, day.DisplayName, true, null));
                else ineligible.Add(new CoverageMember(day.TechnicianUserId, day.DisplayName, true, reason));
            }
        }

        var status = eligible.Count > 0 ? CoverageStatuses.Covered
            : available.Count > 0 ? CoverageStatuses.ScheduleConflict
            : CoverageStatuses.NoAvailableTechnician;
        return Result(status, Sort(eligible), Sort(ineligible));

        CoverageEvaluation Result(string code, IReadOnlyList<CoverageMember> ok, IReadOnlyList<CoverageMember> not)
            => new(propertySiteId, code, site, ok, not, start, end);
    }

    /// <summary>Technicians (of <paramref name="technicianIds"/>, schedulable members of the zone) who can hold at least one bookable slot in the booking horizon.</summary>
    private async Task<IReadOnlySet<Guid>> AvailableInHorizonAsync(IReadOnlyList<Guid> technicianIds, IReadOnlyList<Guid> zoneIds, CancellationToken cancellationToken)
    {
        var found = new HashSet<Guid>();
        var now = time.GetUtcNow();
        var calendar = eligibility.Calendar;
        var booking = (await eligibility.PolicyAsync(cancellationToken)).Booking;
        var (first, last) = calendar.BookingRange(now, booking);
        var byDate = await eligibility.LoadDaysRangeAsync(first, last, new TechnicianScope(technicianIds, zoneIds), cancellationToken);
        foreach (var (date, days) in byDate.OrderBy(p => p.Key))
        {
            var pending = days.Where(d => !found.Contains(d.TechnicianUserId)).ToList();
            if (pending.Count == 0) continue;
            foreach (var window in calendar.Bookable(calendar.CandidateWindows(date, pending), date, now, booking))
            {
                foreach (var day in pending.Where(d => !found.Contains(d.TechnicianUserId)))
                    if (eligibility.Evaluate(day, new EligibilityRequest(window.StartsAtUtc, window.EndsAtUtc, zoneIds)) is null) found.Add(day.TechnicianUserId);
                if (found.Count == technicianIds.Count) return found;
            }
        }
        return found;
    }

    private static bool Schedulable(TechnicianDayState day) => day.IsActiveAccount && day.HasTechnicianRole && day.IsSchedulingActive;

    /// <summary>Any interval: only the account/scheduling rules can fail for a non-schedulable member.</summary>
    private static EligibilityRequest Probe(TechnicianDayState day, IReadOnlyList<Guid> zoneIds)
        => new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), zoneIds);

    private static IReadOnlyList<CoverageMember> Sort(IEnumerable<CoverageMember> members)
        => members.OrderBy(m => m.FullName, StringComparer.Ordinal).ThenBy(m => m.TechnicianUserId).ToList();
}
