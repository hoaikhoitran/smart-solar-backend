using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// What to reserve: an interval on a site. <see cref="RequiredTechnicianId"/> keeps one technician (a confirmed
/// appointment being moved); otherwise a capacity holder is chosen among the eligible technicians.
/// <see cref="IgnoreAppointmentIds"/> are the appointments being replaced (their capacity is reusable).
/// </summary>
public sealed record ReservationSpec(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    Guid PropertySiteId,
    Guid? RequiredTechnicianId,
    IReadOnlySet<Guid> IgnoreAppointmentIds,
    IReadOnlyList<Guid> AdditionalLockTechnicianIds);

/// <summary>Inside the locked transaction: the chosen technician, the site coverage re-read under the site lock, and now.</summary>
public sealed record ReservationContext(TechnicianDayState Technician, SiteCoverage Coverage, DateTimeOffset NowUtc);

/// <summary>Result of the caller's write inside the reservation transaction.</summary>
public sealed record ApplyResult(bool Succeeded, Guid? AppointmentId, string? ErrorCode, string? Message)
{
    /// <summary>
    /// Internal outcome (never returned to clients): the row the caller wanted to change got a new revision in the meantime.
    /// Nothing is written; the caller re-reads and decides whether the change is still allowed and retries (bounded).
    /// </summary>
    public const string StaleCode = "APPLY_STALE_RETRY";

    public static ApplyResult Applied(Guid appointmentId) => new(true, appointmentId, null, null);
    public static ApplyResult Abort(string code, string message) => new(false, null, code, message);
    public static ApplyResult Stale() => new(false, null, StaleCode, "The appointment changed meanwhile.");
}

public sealed record ReservationOutcome(bool Succeeded, Guid? AppointmentId, Guid? TechnicianUserId, string? ErrorCode, string? Message)
{
    public static ReservationOutcome Fail(string code, string message) => new(false, null, null, code, message);
}

/// <summary>
/// Capacity reservation for bookings and reschedules (capacity decision: per-technician hold). Outside any
/// transaction the eligible technicians are ranked (fewest survey commitments that day, then id). Then, in a
/// short transaction, the chosen technician and the site are locked, the technician's day and the site
/// coverage are re-read and the rules re-checked; only then is the caller's write applied. A concurrent
/// change rolls back and retries; the exclusion constraint stays the final guard against double booking.
/// </summary>
public sealed class AppointmentReservationService(
    ISchedulingUnitOfWork unitOfWork,
    TechnicianEligibility eligibility,
    SchedulingOptions options,
    TimeProvider time)
{
    private enum Step { Done, Retry }

    public async Task<ReservationOutcome> ReserveAsync(
        ReservationSpec spec,
        Func<ReservationContext, CancellationToken, Task<ApplyResult>> apply,
        CancellationToken cancellationToken)
    {
        var date = eligibility.Calendar.LocalDateOf(spec.StartsAtUtc);
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts); attempt++)
        {
            var coverage = await unitOfWork.GetSiteCoverageAsync(spec.PropertySiteId, cancellationToken);
            if (CoverageError(coverage) is { } coverageError) return coverageError;

            var request = new EligibilityRequest(spec.StartsAtUtc, spec.EndsAtUtc, coverage.ZoneIds, spec.IgnoreAppointmentIds);
            TechnicianDayState option;
            if (spec.RequiredTechnicianId is { } required)
            {
                var (day, reason) = await eligibility.CheckAsync(required, request, cancellationToken);
                if (reason is not null) return ReservationOutcome.Fail(reason, Describe(reason));
                option = day!;
            }
            else
            {
                var ranked = eligibility.Rank(await eligibility.LoadDaysAsync(date, new TechnicianScope(ZoneIds: coverage.ZoneIds), cancellationToken), request);
                if (ranked.Count == 0)
                    return ReservationOutcome.Fail(SchedulingErrorCodes.SlotUnavailable, "No technician of this zone can take this appointment time.");
                option = ranked[0].Day;
            }

            (Step Step, ApplyResult? Applied) result;
            try
            {
                result = await unitOfWork.ExecuteInTransactionAsync<(Step, ApplyResult?)>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync(spec.AdditionalLockTechnicianIds.Append(option.TechnicianUserId), token);
                    await unitOfWork.LockPropertySiteAsync(spec.PropertySiteId, token);
                    // FOR SHARE: the zone recorded on the appointment cannot be deactivated or lose the commune before this commits.
                    await unitOfWork.LockZonesAsync(coverage.ZoneIds, shared: true, token);

                    var freshCoverage = await unitOfWork.GetSiteCoverageAsync(spec.PropertySiteId, token);
                    var fresh = (await eligibility.LoadDaysAsync(date, new TechnicianScope([option.TechnicianUserId], SchedulableOnly: false), token)).SingleOrDefault();
                    if (fresh is null || freshCoverage.Status != Zones.CoverageStatuses.Covered || freshCoverage.Zone!.Id != coverage.Zone!.Id
                        || eligibility.Evaluate(fresh, request with { AcceptableZoneIds = freshCoverage.ZoneIds }) is not null)
                    {
                        return ((Step.Retry, null), false);
                    }

                    var applied = await apply(new ReservationContext(fresh, freshCoverage, time.GetUtcNow()), token);
                    if (!applied.Succeeded) return ((Step.Done, applied), false);

                    await unitOfWork.SaveChangesAsync(token);
                    return ((Step.Done, applied), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind is SchedulingConflictException.TechnicianOverlap or SchedulingConflictException.StaleAppointment)
            {
                // Another reservation won the race for this technician or appointment: re-evaluate.
                continue;
            }

            if (result.Step == Step.Retry) continue;
            var outcome = result.Applied!;
            return outcome.Succeeded
                ? new ReservationOutcome(true, outcome.AppointmentId, option.TechnicianUserId, null, null)
                : ReservationOutcome.Fail(outcome.ErrorCode!, outcome.Message!);
        }

        return ReservationOutcome.Fail(SchedulingErrorCodes.ConcurrencyConflict,
            "The technician schedule kept changing while the appointment was being reserved. Please try again.");
    }

    /// <summary>
    /// SITE_LOCATION_REQUIRED without a valid commune, NO_ZONE_COVERAGE when no active zone maps the commune, ZONE_NOT_SERVICEABLE when
    /// the zone is marked not serviceable; null when the site is covered.
    /// </summary>
    public static ReservationOutcome? CoverageError(SiteCoverage coverage) => coverage.Status switch
    {
        Zones.CoverageStatuses.SiteLocationRequired => ReservationOutcome.Fail(SchedulingErrorCodes.SiteLocationRequired, CoverageMessages.SiteLocationRequired),
        Zones.CoverageStatuses.NoZoneCoverage => ReservationOutcome.Fail(SchedulingErrorCodes.NoZoneCoverage, CoverageMessages.NoZoneCoverage),
        Zones.CoverageStatuses.ZoneNotServiceable => ReservationOutcome.Fail(SchedulingErrorCodes.ZoneNotServiceable, CoverageMessages.ZoneNotServiceable),
        _ => null,
    };

    public static string Describe(string reason) => reason switch
    {
        EligibilityReasons.TechnicianInactive => "The technician account is not active.",
        EligibilityReasons.SchedulingDisabled => "Scheduling is disabled for this technician.",
        EligibilityReasons.OutsideZone => "The technician is not a member of the site's operational zone.",
        EligibilityReasons.OutsideWorkingHours => "The time is outside the technician's working hours.",
        EligibilityReasons.OnLeave => "The technician is on leave at that time.",
        EligibilityReasons.ScheduleConflict => "The technician has another commitment too close to this time.",
        EligibilityReasons.DailyLimitReached => "The technician reached the daily appointment limit.",
        _ => "The technician cannot take this appointment.",
    };
}

/// <summary>Safe, stable messages of the technician-independent coverage codes (shared by Flow 1 and the Installation Flow).</summary>
public static class CoverageMessages
{
    public const string SiteLocationRequired =
        "The property site has no valid administrative location. Set the province and the current (post-2025) commune of the site first.";

    public const string NoZoneCoverage = "No active operational zone serves the commune of this property site yet.";

    public const string ZoneNotServiceable = "The operational zone of this property site is currently not serviceable.";

    public const string NoZoneTechnician =
        "No technician serves this area yet. You can request a dispatch: our staff will arrange a technician and send you a proposal.";

    public const string NoAvailableTechnician =
        "No technician of this area is free within the booking horizon. You can request a dispatch: our staff will arrange a technician.";

    public const string ScheduleConflict =
        "No technician of this area is free at this time. Choose another time, or request a dispatch: our staff will arrange a technician.";

    public const string NoZoneCoverageDispatch =
        NoZoneCoverage + " You can request a dispatch: our staff will arrange a technician and send you a proposal.";

    /// <summary>Customer-safe explanation of a coverage status (null for COVERED).</summary>
    public static string? Describe(string status) => status switch
    {
        Zones.CoverageStatuses.SiteLocationRequired => SiteLocationRequired,
        Zones.CoverageStatuses.NoZoneCoverage => NoZoneCoverageDispatch,
        Zones.CoverageStatuses.ZoneNotServiceable => ZoneNotServiceable,
        Zones.CoverageStatuses.NoZoneTechnician => NoZoneTechnician,
        Zones.CoverageStatuses.NoAvailableTechnician => NoAvailableTechnician,
        Zones.CoverageStatuses.ScheduleConflict => ScheduleConflict,
        _ => null,
    };
}
