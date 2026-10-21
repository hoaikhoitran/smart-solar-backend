using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// Who confirms a technician on an appointment, and how. <see cref="RequiredSalesOwnerId"/> (Sales fallback assignment) is
/// re-checked inside the transaction: when the request's Sales owner is no longer that user the assignment fails with
/// SURVEY_REQUEST_NOT_OWNED and nothing changes.
/// </summary>
public sealed record AssignmentSpec(
    Guid AppointmentId,
    Guid TechnicianUserId,
    Guid ActorUserId,
    string Source,
    string? Reason,
    int? ExpectedRevision,
    Guid? RequiredSalesOwnerId = null);

/// <summary>The appointment after the operation. <see cref="Changed"/> is false for an idempotent repeat.</summary>
public sealed record AssignmentResult(AppointmentSnapshot Appointment, bool Changed, Guid? PreviousTechnicianUserId, IReadOnlyList<Guid> MovedHoldIds);

/// <summary>A pending hold moved to another technician, or escalated without a holder.</summary>
public sealed record RehomeResult(Guid AppointmentId, Guid? NewHolderUserId);

/// <summary>
/// The pending capacity holds of one technician that conflict with an interval the technician is being committed to
/// (survey appointment or installation assignment, buffer included) and must be handed to other technicians, plus the
/// technicians who could take them (lock them together with the technician). Built by
/// <see cref="AppointmentAssignmentService.PlanSpanAsync"/> or internally for survey intervals.
/// </summary>
public sealed class HoldRehomePlan
{
    internal HoldRehomePlan(IReadOnlyList<HoldPlanItem> holds, IReadOnlyList<Guid> candidates)
    {
        Holds = holds;
        CandidateTechnicianIds = candidates;
    }

    internal IReadOnlyList<HoldPlanItem> Holds { get; }

    public IReadOnlyList<Guid> HoldIds => Holds.Select(h => h.Hold.Id).ToList();

    public IReadOnlyList<Guid> CandidateTechnicianIds { get; }

    /// <summary>True when <paramref name="fresh"/> (re-planned under the locks) needs nothing this plan did not lock or move.</summary>
    public bool Covers(HoldRehomePlan fresh, IReadOnlySet<Guid> locked)
        => fresh.CandidateTechnicianIds.All(locked.Contains) && fresh.Holds.All(f => Holds.Any(h => h.Hold.Id == f.Hold.Id));
}

internal sealed record HoldPlanItem(AppointmentSnapshot Hold, SiteCoverage Coverage);

/// <summary>Holds detached from a technician inside a transaction, with their chosen new holders; attach after the caller's write.</summary>
public sealed class RehomeSession
{
    internal RehomeSession(IReadOnlyList<DetachedHold> detached, IReadOnlyDictionary<Guid, Guid> targets)
    {
        Detached = detached;
        Targets = targets;
    }

    internal IReadOnlyList<DetachedHold> Detached { get; }
    internal IReadOnlyDictionary<Guid, Guid> Targets { get; }

    public IReadOnlyList<Guid> MovedHoldIds => Detached.Select(d => d.Entity.Id).ToList();
}

internal sealed record DetachedHold(TechnicianAppointment Entity, string OriginalStatus, Guid? OriginalHolder);

/// <summary>
/// Confirms technicians on appointments (self-acceptance, Sales assignment, Manager override), releases them,
/// and moves pending capacity holds. All paths share one algorithm:
/// <list type="number">
/// <item>Outside a transaction: read the appointment, check the target technician with
/// <see cref="TechnicianEligibility"/> (ignoring the target's own pending holds), and work out which of the
/// target's holds conflict with the appointment and must be re-homed, plus the technicians who could take them.</item>
/// <item>In one transaction: lock every involved technician profile (sorted), re-read and re-check everything,
/// choose a new holder for each conflicting hold, then write: detach those holds, confirm the appointment with a
/// revision-checked update, and re-attach the holds to their new holders (the order keeps the exclusion
/// constraint satisfied at every statement). A hold that cannot be re-homed fails the whole operation with
/// TECHNICIAN_SCHEDULE_CONFLICT and nothing changes.</item>
/// <item>Exactly one of concurrent accepts / assignments wins: the loser sees a stale revision and gets 409
/// APPOINTMENT_ALREADY_TAKEN, or 200 unchanged when it was the same technician.</item>
/// </list>
/// </summary>
public sealed class AppointmentAssignmentService(
    ISchedulingUnitOfWork unitOfWork,
    TechnicianEligibility eligibility,
    SchedulingOptions options,
    TimeProvider time,
    IDispatchUnitOfWork dispatch)
{
    private enum Step { Done, Retry }

    public TechnicianEligibility Eligibility => eligibility;

    public async Task<SchedulingResult<AssignmentResult>> AssignAsync(AssignmentSpec spec, CancellationToken cancellationToken)
    {
        var self = spec.Source == AssignmentSources.SelfAccepted;
        var extraLocks = new HashSet<Guid>();
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts) + 1; attempt++)
        {
            var now = time.GetUtcNow();
            var snapshot = await unitOfWork.GetAppointmentAsync(spec.AppointmentId, cancellationToken);
            if (snapshot is null) return Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");

            var coverage = await unitOfWork.GetSiteCoverageAsync(snapshot.PropertySiteId, cancellationToken);
            // A site without a valid commune (legacy booking) cannot be matched to any zone: the customer must set its location first.
            if (!coverage.HasLocation && AppointmentStatuses.IsChangeable(snapshot.Status) && Precheck(snapshot, spec, self, now) is not { Succeeded: true })
                return SiteLocationRequired();
            // A technician only learns that an appointment exists (or its state) when they are a member of the site's active zone.
            if (self && !await ServesAsync(spec.TechnicianUserId, coverage, cancellationToken))
                return Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");

            var precheck = Precheck(snapshot, spec, self, now);
            if (precheck is not null) return precheck;
            if (!self && await OnSiteSurveyBlocksAsync(snapshot, spec.TechnicianUserId, cancellationToken) is { } started) return started;

            var technician = await unitOfWork.GetTechnicianAsync(spec.TechnicianUserId, cancellationToken);
            if (technician is null || !technician.HasTechnicianRole) return Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");

            var (plan, planError) = await PlanIntervalAsync(spec.TechnicianUserId, snapshot.StartsAt, snapshot.EndsAt, coverage.ZoneIds,
                new HashSet<Guid> { snapshot.Id }, cancellationToken);
            if (planError is not null) return Fail(planError, ReservationDescribe(planError));

            var locks = new HashSet<Guid>(extraLocks) { spec.TechnicianUserId };
            if (snapshot.ReservedTechnicianUserId is { } holder) locks.Add(holder);
            if (snapshot.AssignedTechnicianUserId is { } assigned) locks.Add(assigned);
            locks.UnionWith(plan!.CandidateTechnicianIds);

            (Step Step, SchedulingResult<AssignmentResult>? Result) outcome;
            try
            {
                outcome = await unitOfWork.ExecuteInTransactionAsync<(Step, SchedulingResult<AssignmentResult>?)>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync(locks, token);
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(spec.AppointmentId, token);
                    if (appointment is null || appointment.Revision != snapshot.Revision) return ((Step.Retry, null), false);
                    if (appointment.StartsAt <= time.GetUtcNow())
                        return ((Step.Done, Fail(SchedulingErrorCodes.InvalidBookingState, "Started or past appointments cannot be assigned.")), false);
                    // Under the locks of both technicians: the confirmed technician cannot have started the survey meanwhile.
                    if (!self && await OnSiteSurveyBlocksAsync(snapshot, spec.TechnicianUserId, token) is { } blocked) return ((Step.Done, blocked), false);

                    var freshCoverage = await unitOfWork.GetSiteCoverageAsync(appointment.PropertySiteId, token);
                    if (!freshCoverage.HasLocation) return ((Step.Done, SiteLocationRequired()), false);
                    var (freshPlan, freshError) = await PlanIntervalAsync(spec.TechnicianUserId, snapshot.StartsAt, snapshot.EndsAt, freshCoverage.ZoneIds,
                        new HashSet<Guid> { snapshot.Id }, token);
                    if (freshError is not null) return ((Step.Done, Fail(freshError, ReservationDescribe(freshError))), false);

                    var stamp = time.GetUtcNow();
                    var (status, session) = await DetachForRehomeAsync(plan!, freshPlan!, locks, spec.TechnicianUserId, new HashSet<Guid> { appointment.Id }, stamp, token);
                    if (status == RehomeStatus.Retry)
                    {
                        // A hold that appeared meanwhile may need technicians we did not lock: retry with them locked.
                        extraLocks.UnionWith(freshPlan!.CandidateTechnicianIds);
                        return ((Step.Retry, null), false);
                    }
                    if (status == RehomeStatus.Conflict)
                        return ((Step.Done, Fail(SchedulingErrorCodes.TechnicianScheduleConflict,
                            "The technician has a pending appointment too close to this time that no other technician can take over.")), false);

                    var previousAssignee = appointment.AssignedTechnicianUserId;
                    var previousHolder = appointment.ReservedTechnicianUserId;
                    Confirm(appointment, spec, self, stamp);
                    await unitOfWork.SaveChangesAsync(token);

                    await AttachAsync(session!, spec.TechnicianUserId, spec.ActorUserId, stamp, token);

                    if (appointment.SurveyRequestId is { } requestId && !await unitOfWork.SetSurveyRequestScheduleAsync(requestId, appointment.StartsAt, token))
                        return ((Step.Done, Fail(SchedulingErrorCodes.InvalidBookingState, "The survey request can no longer be scheduled.")), false);

                    // The request row is now write-locked (schedule update above): the owner read here cannot change before commit.
                    var owner = appointment.SurveyRequestId is { } rid ? (await unitOfWork.GetSurveyRequestContextAsync(rid, token))?.AssignedSaleId : null;
                    if (spec.RequiredSalesOwnerId is { } requiredOwner && owner != requiredOwner)
                        return ((Step.Done, Fail(SchedulingErrorCodes.SurveyRequestNotOwned,
                            "The survey request has another Sales owner now. Only the Sales owner can assign a technician.")), false);
                    RecordConfirmation(appointment, spec, self, previousAssignee, previousHolder, owner, stamp);
                    await unitOfWork.SaveChangesAsync(token);

                    var result = new AssignmentResult(Snapshot(appointment), true, previousAssignee, session!.MovedHoldIds);
                    return ((Step.Done, SchedulingResult<AssignmentResult>.Ok(result)), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.TechnicianOverlap)
            {
                return Fail(SchedulingErrorCodes.TechnicianScheduleConflict, "The technician's schedule changed and now conflicts with this appointment.");
            }
            catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.StaleAppointment)
            {
                outcome = (Step.Retry, null);
            }

            if (outcome.Step == Step.Done) return outcome.Result!;
        }

        // Exhausted retries: explain with the latest state.
        var latest = await unitOfWork.GetAppointmentAsync(spec.AppointmentId, cancellationToken);
        return latest is not null && Precheck(latest, spec, self, time.GetUtcNow()) is { } final
            ? final
            : Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment kept changing. Reload and try again.");
    }

    /// <summary>
    /// Reserves [start, end) on a site for one REQUIRED technician (a confirmed technician kept on a reschedule, or a technician
    /// named by Sales), with the same plan/re-home logic as acceptance: the technician's own pending holds are ignored for
    /// eligibility, and those conflicting with the interval (or pushing the day over the daily limit) are handed to other
    /// eligible technicians in the same transaction. Order: detach the holds, run <paramref name="apply"/> (the caller's write),
    /// attach the holds to their new holders, commit. Failure codes are eligibility codes (TECHNICIAN_SCHEDULE_CONFLICT when a
    /// hold cannot be handed over), coverage codes, the caller's abort code, or APPOINTMENT_CONCURRENCY_CONFLICT.
    /// </summary>
    /// <param name="crossZone">
    /// Manual cross-zone dispatch (a Manager/Admin authorized a technician who is not a member of the site's zone, or the site's
    /// commune has no active zone at all): ONLY the zone-membership rule is skipped, and NO_ZONE_COVERAGE is accepted. Every other
    /// rule (account, scheduling flag, working hours, leave, buffer/overlap, daily limit, hold re-homing), the locks, the in-lock
    /// re-checks and the exclusion constraint still apply; SITE_LOCATION_REQUIRED still refuses.
    /// </param>
    public async Task<ReservationOutcome> ReserveForTechnicianAsync(ReservationSpec spec, Guid actorUserId,
        Func<ReservationContext, CancellationToken, Task<ApplyResult>> apply, CancellationToken cancellationToken, bool crossZone = false)
    {
        var technicianId = spec.RequiredTechnicianId ?? throw new ArgumentException("A required technician is needed.", nameof(spec));
        var date = eligibility.Calendar.LocalDateOf(spec.StartsAtUtc);
        var extraLocks = new HashSet<Guid>();
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts) + 1; attempt++)
        {
            var coverage = await unitOfWork.GetSiteCoverageAsync(spec.PropertySiteId, cancellationToken);
            if (DispatchCoverageError(coverage, crossZone) is { } coverageError) return coverageError;
            IReadOnlyCollection<Guid>? zoneIds = crossZone ? null : coverage.ZoneIds;

            var (plan, reason) = await PlanIntervalAsync(technicianId, spec.StartsAtUtc, spec.EndsAtUtc, zoneIds, spec.IgnoreAppointmentIds, cancellationToken);
            if (reason is not null) return ReservationOutcome.Fail(reason, ReservationDescribe(reason));

            var locks = new HashSet<Guid>(extraLocks) { technicianId };
            locks.UnionWith(spec.AdditionalLockTechnicianIds);
            locks.UnionWith(plan!.CandidateTechnicianIds);

            (Step Step, ApplyResult? Applied) result;
            try
            {
                result = await unitOfWork.ExecuteInTransactionAsync<(Step, ApplyResult?)>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync(locks, token);
                    await unitOfWork.LockPropertySiteAsync(spec.PropertySiteId, token);
                    await unitOfWork.LockZonesAsync(coverage.ZoneIds, shared: true, token);

                    var freshCoverage = await unitOfWork.GetSiteCoverageAsync(spec.PropertySiteId, token);
                    if (DispatchCoverageError(freshCoverage, crossZone) is not null || freshCoverage.Zone?.Id != coverage.Zone?.Id) return ((Step.Retry, null), false);
                    var (freshPlan, freshReason) = await PlanIntervalAsync(technicianId, spec.StartsAtUtc, spec.EndsAtUtc, crossZone ? null : freshCoverage.ZoneIds,
                        spec.IgnoreAppointmentIds, token);
                    if (freshReason is not null) return ((Step.Done, ApplyResult.Abort(freshReason, ReservationDescribe(freshReason))), false);

                    var now = time.GetUtcNow();
                    var (status, session) = await DetachForRehomeAsync(plan!, freshPlan!, locks, technicianId, spec.IgnoreAppointmentIds, now, token);
                    if (status == RehomeStatus.Retry)
                    {
                        extraLocks.UnionWith(freshPlan!.CandidateTechnicianIds);
                        return ((Step.Retry, null), false);
                    }
                    if (status == RehomeStatus.Conflict)
                        return ((Step.Done, ApplyResult.Abort(SchedulingErrorCodes.TechnicianScheduleConflict,
                            "The technician has a pending appointment too close to this time that no other technician can take over.")), false);

                    var day = (await eligibility.LoadDaysAsync(date, new TechnicianScope([technicianId], SchedulableOnly: false), token)).Single();
                    var applied = await apply(new ReservationContext(day, freshCoverage, now), token);
                    if (!applied.Succeeded) return ((Step.Done, applied), false);
                    await unitOfWork.SaveChangesAsync(token);

                    await AttachAsync(session!, technicianId, actorUserId, now, token);
                    return ((Step.Done, applied), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind is SchedulingConflictException.TechnicianOverlap or SchedulingConflictException.StaleAppointment)
            {
                continue;
            }

            if (result.Step == Step.Retry) continue;
            var outcome = result.Applied!;
            return outcome.Succeeded
                ? new ReservationOutcome(true, outcome.AppointmentId, technicianId, null, null)
                : ReservationOutcome.Fail(outcome.ErrorCode!, outcome.Message!);
        }

        return ReservationOutcome.Fail(SchedulingErrorCodes.ConcurrencyConflict,
            "The technician schedule kept changing while the appointment was being reserved. Please try again.");
    }

    /// <summary>
    /// The usual coverage refusal; an authorized cross-zone dispatch also accepts NO_ZONE_COVERAGE (no zone to be a member of) and
    /// ZONE_NOT_SERVICEABLE (the zone takes no new self-service work, staff place it by hand). SITE_LOCATION_REQUIRED always refuses.
    /// </summary>
    private static ReservationOutcome? DispatchCoverageError(SiteCoverage coverage, bool crossZone)
        => crossZone && coverage.Status is Zones.CoverageStatuses.NoZoneCoverage or Zones.CoverageStatuses.ZoneNotServiceable
            ? null
            : AppointmentReservationService.CoverageError(coverage);

    /// <summary>
    /// Whether <paramref name="technicianUserId"/> could be confirmed on [start, end) now (no write, no lock): the same plan as an
    /// assignment, so their own pending holds are not obstacles when they can be re-homed. <paramref name="zoneIds"/> null = cross-zone
    /// (zone membership not required). Returns the eligibility reason, or null, and whether own holds would be re-homed.
    /// </summary>
    public async Task<(string? Reason, bool RequiresHoldRehoming)> EvaluateConfirmationAsync(Guid technicianUserId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        IReadOnlyCollection<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var (plan, reason) = await PlanIntervalAsync(technicianUserId, startsAt, endsAt, zoneIds, new HashSet<Guid>(), cancellationToken);
        return (reason, plan is { } p && p.HoldIds.Count > 0);
    }

    /// <summary>
    /// The technician can no longer attend a confirmed future appointment. The capacity hold moves to another eligible
    /// technician and the appointment is open again with a fresh acceptance deadline =
    /// LEAST(now + scheduling.acceptanceWindowMinutes, starts_at − scheduling.acceptanceMinimumLeadMinutes): PENDING_ACCEPTANCE while that
    /// deadline is in the future, ESCALATED otherwise. When nobody can take the hold it escalates without a holder. The request returns to
    /// Pending. The release is a NEW timing event: the new deadline uses the settings current now (stored with its snapshot), and the
    /// release cut-off is <c>onsiteSurvey.earlyStartMinutes</c> of the current settings.
    /// <para>
    /// An appointment placed by hand from a dispatch request (assignment source MANAGER_DISPATCH, or with a cross-zone authorization)
    /// is never re-homed to zone members — there may be none, and its technician was chosen deliberately. It goes back to the
    /// Manager/Admin dispatch queue instead (see <see cref="ReturnToDispatchAsync"/>): the customer's survey request is kept and
    /// Pending again, so staff can assign another technician (cross-zone approval and every scheduling check apply again) or propose
    /// another time, and the customer never has to cancel and submit a new request.
    /// </para>
    /// </summary>
    public async Task<SchedulingResult<AppointmentSnapshot>> ReleaseAsync(Guid technicianUserId, Guid appointmentId, string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts); attempt++)
        {
            var snapshot = await unitOfWork.GetAppointmentAsync(appointmentId, cancellationToken);
            if (snapshot is null || snapshot.AssignedTechnicianUserId != technicianUserId || !AppointmentStatuses.IsConfirmed(snapshot.Status))
                return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");
            if (snapshot.StartsAt <= time.GetUtcNow())
                return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.InvalidBookingState, "Started or past appointments cannot be released.");
            var policy = await eligibility.PolicyAsync(cancellationToken);
            // From the earliest on-site survey start onwards the technician is expected on site: staff must reassign instead.
            if (snapshot.StartsAt.AddMinutes(-Math.Max(0, policy.OnSiteSurveyEarlyStartMinutes)) <= time.GetUtcNow())
                return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.InvalidBookingState,
                    $"Appointments can be released only until {policy.OnSiteSurveyEarlyStartMinutes} minutes before the start. Please contact your Sales owner.");
            if (snapshot.SurveyRequestId is { } surveyId && await unitOfWork.HasOnSiteSurveyInProgressAsync(surveyId, cancellationToken))
                return OnSiteSurveyStarted<AppointmentSnapshot>();

            var coverage = await unitOfWork.GetSiteCoverageAsync(snapshot.PropertySiteId, cancellationToken);
            var plan = new List<HoldPlanItem> { new(snapshot, coverage) };
            var locks = new HashSet<Guid>(await CandidatesAsync(plan, cancellationToken)) { technicianUserId };

            try
            {
                var (step, result) = await unitOfWork.ExecuteInTransactionAsync<(Step, SchedulingResult<AppointmentSnapshot>?)>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync(locks, token);
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(appointmentId, token);
                    if (appointment is null || appointment.Revision != snapshot.Revision) return ((Step.Retry, null), false);
                    // Under the technician lock: the on-site survey cannot have been started meanwhile (starting it takes the same lock).
                    if (appointment.SurveyRequestId is { } requestInLock && await unitOfWork.HasOnSiteSurveyInProgressAsync(requestInLock, token))
                        return ((Step.Done, OnSiteSurveyStarted<AppointmentSnapshot>()), false);

                    var now = time.GetUtcNow();
                    if (appointment.AssignmentSource == AssignmentSources.ManagerDispatch || appointment.CrossZoneAuthorizationId is not null)
                        return ((Step.Done, await ReturnToDispatchAsync(appointment, technicianUserId, reason, policy, now, token)), true);

                    var target = await PickHolderAsync(snapshot, await unitOfWork.GetSiteCoverageAsync(snapshot.PropertySiteId, token), locks,
                        exclude: technicianUserId, cache: new Dictionary<DateOnly, List<TechnicianDayState>>(), token);

                    appointment.AssignedTechnicianUserId = null;
                    appointment.AssignedAt = null;
                    appointment.AcceptedAt = null;
                    appointment.AssignmentSource = null;
                    appointment.AssignmentReason = null;
                    appointment.AssignedBySaleUserId = null;
                    appointment.ReservedTechnicianUserId = target;
                    // A fresh acceptance window starts now, still capped by the minimum lead before the start.
                    var deadline = Appointments.Deadline(now, appointment.StartsAt, policy);
                    appointment.AcceptanceDeadlineAt = deadline;
                    appointment.AcceptanceWindowMinutes = policy.AcceptanceWindowMinutes;
                    appointment.SettingsVersion = policy.Version;
                    appointment.AcceptanceReminderSentAt = null;   // a new acceptance window may get its own reminder
                    var escalate = target is null || deadline <= now;
                    appointment.Status = escalate ? AppointmentStatuses.Escalated : AppointmentStatuses.PendingAcceptance;
                    appointment.EscalatedAt = escalate ? now : null;
                    appointment.UpdatedAt = now;
                    appointment.Revision++;
                    await unitOfWork.SaveChangesAsync(token);

                    if (appointment.SurveyRequestId is { } requestId) await unitOfWork.SetSurveyRequestScheduleAsync(requestId, null, token);
                    var owner = appointment.SurveyRequestId is { } rid ? (await unitOfWork.GetSurveyRequestContextAsync(rid, token))?.AssignedSaleId : null;
                    AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Released, technicianUserId, now,
                        previousTechnicianUserId: technicianUserId, newTechnicianUserId: target, reason: reason, notify: [owner, appointment.BookedByUserId]);
                    if (escalate)
                        AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Escalated, null, now,
                            newTechnicianUserId: target, reason: target is null ? "NO_HOLDER_AVAILABLE" : "DEADLINE_PASSED", notify: [owner]);
                    await unitOfWork.SaveChangesAsync(token);
                    return ((Step.Done, SchedulingResult<AppointmentSnapshot>.Ok(Snapshot(appointment))), true);
                }, cancellationToken);
                if (step == Step.Done) return result!;
            }
            catch (SchedulingConflictException ex) when (ex.Kind is SchedulingConflictException.StaleAppointment or SchedulingConflictException.TechnicianOverlap)
            {
                // Someone changed the appointment or the new holder's day: re-read and try again.
            }
        }

        return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment kept changing. Reload and try again.");
    }

    /// <summary>
    /// Inside the release transaction (technician locked, appointment row locked at the expected revision): the released dispatch
    /// appointment is CANCELLED (RELEASED_TO_DISPATCH) with its RELEASED and CANCELLED events, the survey request returns to Pending
    /// and a new OPEN dispatch request (reason ASSIGNMENT_RELEASED, fresh SLA snapshot, the released time as preferred window, the
    /// customer note of the previous request) is created in the same transaction. Both dispatch requests record the release; staff
    /// and the customer are notified. The previous request stays ASSIGNED as history. The site lock (technician → site, as for
    /// bookings) and the one-active-request unique index keep a concurrent booking or dispatch creation from interleaving.
    /// </summary>
    private async Task<SchedulingResult<AppointmentSnapshot>> ReturnToDispatchAsync(TechnicianAppointment appointment, Guid technicianUserId, string reason,
        SchedulingPolicySnapshot policy, DateTimeOffset now, CancellationToken token)
    {
        if (appointment.SurveyRequestId is not { } surveyRequestId)
            return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        await unitOfWork.LockPropertySiteAsync(appointment.PropertySiteId, token);
        var survey = await unitOfWork.GetSurveyRequestContextAsync(surveyRequestId, token);
        if (survey is null) return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        var previous = await dispatch.FindLatestForSurveyRequestAsync(surveyRequestId, token);

        appointment.AssignedTechnicianUserId = null;
        appointment.ReservedTechnicianUserId = null;
        appointment.AssignedAt = null;
        appointment.AcceptedAt = null;
        appointment.AssignmentSource = null;
        appointment.AssignmentReason = null;
        appointment.AssignedBySaleUserId = null;
        Appointments.Cancel(appointment, CancellationReasons.ReleasedToDispatch, now);
        await unitOfWork.SaveChangesAsync(token);
        await unitOfWork.SetSurveyRequestScheduleAsync(surveyRequestId, null, token);

        var coverage = await unitOfWork.GetSiteCoverageAsync(appointment.PropertySiteId, token);
        var request = DispatchRequests.Open(surveyRequestId, appointment.PropertySiteId, survey.CustomerUserId, coverage.Zone?.Id,
            DispatchReasons.AssignmentReleased, previous?.CustomerNote, policy, now, appointment.Id);
        dispatch.Add(request);
        dispatch.Add(new DispatchPreferredWindow { Id = Guid.NewGuid(), DispatchRequestId = request.Id, StartsAt = appointment.StartsAt, EndsAt = appointment.EndsAt });

        AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Released, technicianUserId, now,
            previousTechnicianUserId: technicianUserId, reason: reason, notify: [survey.AssignedSaleId]);
        AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Cancelled, technicianUserId, now,
            reason: $"{CancellationReasons.ReleasedToDispatch}: dispatch request {request.Id}");
        var detail = $"appointment {appointment.Id} ({appointment.StartsAt:O}) released by technician {technicianUserId}: {reason}";
        var staff = await DispatchHistory.StaffRecipientsAsync(dispatch, request, token);
        DispatchHistory.Record(dispatch, request.Id, DispatchEventTypes.AssignmentReleased, technicianUserId, now,
            Truncate(previous is null ? detail : $"{detail}; previous dispatch request {previous.Id}"), null, [.. staff, survey.CustomerUserId]);
        if (previous is not null)
            DispatchHistory.Record(dispatch, previous.Id, DispatchEventTypes.AssignmentReleased, technicianUserId, now,
                Truncate($"{detail}; reopened as dispatch request {request.Id}"), null, []);
        await dispatch.SaveChangesAsync(token);
        return SchedulingResult<AppointmentSnapshot>.Ok(Snapshot(appointment));
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];

    /// <summary>Technicians who could take over these holds (schedulable members of their zones). Lock them with the caller's technician.</summary>
    public async Task<IReadOnlyList<Guid>> RehomeCandidatesAsync(IEnumerable<Guid> holdIds, CancellationToken cancellationToken)
        => await CandidatesAsync(await HoldPlanAsync(holdIds, cancellationToken), cancellationToken);

    /// <summary>
    /// Inside the caller's transaction (after locking <paramref name="lockedTechnicians"/>, which includes the
    /// technician): moves each pending hold of <paramref name="technicianUserId"/> to another eligible technician,
    /// or escalates it without a holder. Used when a technician leaves a zone, goes on leave, changes working
    /// hours, lowers the daily limit or stops scheduling. The caller saves.
    /// </summary>
    public async Task<IReadOnlyList<RehomeResult>> RehomeHoldsAsync(Guid technicianUserId, IReadOnlyList<Guid> holdIds, IReadOnlySet<Guid> lockedTechnicians,
        Guid actorUserId, string reason, CancellationToken token)
    {
        var results = new List<RehomeResult>();
        var cache = new Dictionary<DateOnly, List<TechnicianDayState>>();
        var now = time.GetUtcNow();
        foreach (var id in holdIds.Distinct())
        {
            var hold = await unitOfWork.FindAppointmentForUpdateAsync(id, token);
            if (hold is null || !AppointmentStatuses.IsOpen(hold.Status) || hold.ReservedTechnicianUserId != technicianUserId) continue;
            var snapshot = Snapshot(hold);
            var target = await PickHolderAsync(snapshot, await unitOfWork.GetSiteCoverageAsync(hold.PropertySiteId, token), lockedTechnicians,
                exclude: technicianUserId, cache, token);
            hold.ReservedTechnicianUserId = target;
            if (target is null)
            {
                hold.Status = AppointmentStatuses.Escalated;
                hold.EscalatedAt = now;
            }
            hold.UpdatedAt = now;
            hold.Revision++;
            var owner = hold.SurveyRequestId is { } rid ? (await unitOfWork.GetSurveyRequestContextAsync(rid, token))?.AssignedSaleId : null;
            AppointmentHistory.Record(unitOfWork, hold, target is null ? AppointmentEventTypes.Escalated : AppointmentEventTypes.HoldMoved, actorUserId, now,
                technicianUserId, target, reason: target is null ? $"{reason}: NO_HOLDER_AVAILABLE" : reason, notify: target is null ? [owner] : []);
            results.Add(new RehomeResult(hold.Id, target));
        }
        return results;
    }

    /// <summary>
    /// Plan for a (possibly multi-day) installation interval [start, end) of the technician: the technician is checked with
    /// <see cref="EligibilityRules.CheckSpan"/> ignoring their own pending survey holds (and <paramref name="ignoreCommitmentIds"/>),
    /// and every pending hold of theirs that conflicts with the interval (buffer included) must be re-homed. Confirmed survey
    /// appointments and other installation assignments still block. Returns the eligibility reason when ineligible.
    /// </summary>
    public async Task<(HoldRehomePlan? Plan, string? Reason)> PlanSpanAsync(Guid technicianUserId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc,
        IReadOnlyCollection<Guid>? zoneIds, IReadOnlySet<Guid>? ignoreCommitmentIds, CancellationToken cancellationToken)
    {
        var days = new List<TechnicianDayState>();
        var last = eligibility.Calendar.LocalDateOf(endsAtUtc.AddTicks(-1));
        for (var date = eligibility.Calendar.LocalDateOf(startsAtUtc); date <= last; date = date.AddDays(1))
            days.AddRange(await eligibility.LoadDaysAsync(date, new TechnicianScope([technicianUserId], SchedulableOnly: false), cancellationToken));
        if (days.Count == 0) return (null, EligibilityReasons.TechnicianInactive);

        var holds = days.SelectMany(d => d.Appointments).Where(a => a.IsPendingHold).DistinctBy(a => a.AppointmentId).ToList();
        var ignore = holds.Select(h => h.AppointmentId).Concat(ignoreCommitmentIds ?? new HashSet<Guid>()).ToHashSet();
        var request = new EligibilityRequest(startsAtUtc, endsAtUtc, zoneIds, ignore, CountsTowardDailyLimit: false);
        if (EligibilityRules.CheckSpan(days, request, eligibility.Buffer) is { } reason) return (null, reason);

        var toMove = holds.Where(h => EligibilityRules.Conflicts(h, startsAtUtc, endsAtUtc, eligibility.Buffer)).Select(h => h.AppointmentId);
        var items = await HoldPlanAsync(toMove, cancellationToken);
        return (new HoldRehomePlan(items, await CandidatesAsync(items, cancellationToken)), null);
    }

    public enum RehomeStatus { Ready, Retry, Conflict }

    /// <summary>
    /// Inside the caller's transaction, after locking <paramref name="locked"/> (the technician, every candidate of
    /// <paramref name="planned"/>, and whoever else the caller needs) and re-planning (<paramref name="fresh"/>): chooses a new
    /// holder for every hold of the fresh plan among the locked, eligible technicians (never <paramref name="technicianUserId"/>;
    /// <paramref name="leaving"/> are commitments that leave their holders in this transaction, so those holders may take a hold in
    /// their place) and detaches the holds (saved). Retry = the fresh plan needs a hold or a technician that was not planned/locked
    /// (roll back and retry with <see cref="HoldRehomePlan.CandidateTechnicianIds"/> of the fresh plan locked); Conflict = some hold
    /// cannot be handed over (roll back, TECHNICIAN_SCHEDULE_CONFLICT). After the caller's own write, call <see cref="AttachAsync"/>.
    /// </summary>
    public async Task<(RehomeStatus Status, RehomeSession? Session)> DetachForRehomeAsync(HoldRehomePlan planned, HoldRehomePlan fresh, IReadOnlySet<Guid> locked,
        Guid technicianUserId, IReadOnlySet<Guid>? leaving, DateTimeOffset now, CancellationToken token)
    {
        if (!planned.Covers(fresh, locked)) return (RehomeStatus.Retry, null);
        var targets = await ChooseTargetsAsync(fresh.Holds, locked, technicianUserId, leaving, token);
        if (targets is null) return (RehomeStatus.Conflict, null);
        var detached = await DetachAsync(fresh.Holds, now, token);
        return detached is null ? (RehomeStatus.Retry, null) : (RehomeStatus.Ready, new RehomeSession(detached, targets));
    }

    /// <summary>Each detached hold gets its new holder and its original status back (HOLD_MOVED events). The caller's transaction commits.</summary>
    public async Task AttachAsync(RehomeSession session, Guid fromTechnician, Guid actor, DateTimeOffset now, CancellationToken token)
    {
        if (session.Detached.Count == 0) return;
        foreach (var d in session.Detached)
        {
            d.Entity.ReservedTechnicianUserId = session.Targets[d.Entity.Id];
            d.Entity.Status = d.OriginalStatus;
            AppointmentHistory.Record(unitOfWork, d.Entity, AppointmentEventTypes.HoldMoved, actor, now, d.OriginalHolder ?? fromTechnician, session.Targets[d.Entity.Id],
                reason: "HOLDER_CONFIRMED_ON_ANOTHER_APPOINTMENT");
        }
        await unitOfWork.SaveChangesAsync(token);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>True when the technician has an active membership in the site's (active) zone.</summary>
    private async Task<bool> ServesAsync(Guid technicianUserId, SiteCoverage coverage, CancellationToken token)
    {
        if (coverage.Zone is not { } zone) return false;
        var memberships = await unitOfWork.ListTechnicianZonesAsync(technicianUserId, token);
        return memberships.Any(m => m.Id == zone.Id);
    }

    /// <summary>Staff may not hand a confirmed appointment to someone else once its technician started the on-site survey.</summary>
    private async Task<SchedulingResult<AssignmentResult>?> OnSiteSurveyBlocksAsync(AppointmentSnapshot appointment, Guid newTechnicianUserId, CancellationToken token)
        => AppointmentStatuses.IsConfirmed(appointment.Status) && appointment.AssignedTechnicianUserId is { } current && current != newTechnicianUserId
           && appointment.SurveyRequestId is { } requestId && await unitOfWork.HasOnSiteSurveyInProgressAsync(requestId, token)
            ? Fail(SchedulingErrorCodes.OnSiteSurveyInProgress,
                "The technician already started the on-site survey for this appointment; it cannot be reassigned now.")
            : null;

    internal static SchedulingResult<T> OnSiteSurveyStarted<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.OnSiteSurveyInProgress,
        "The technician already started the on-site survey for this appointment; it cannot be released, moved or cancelled now.");

    private static SchedulingResult<AssignmentResult>? Precheck(AppointmentSnapshot snapshot, AssignmentSpec spec, bool self, DateTimeOffset now)
    {
        // Same technician again (retry, double click, or staff confirming what is already confirmed): unchanged.
        if (snapshot.AssignedTechnicianUserId == spec.TechnicianUserId && AppointmentStatuses.IsConfirmed(snapshot.Status))
            return SchedulingResult<AssignmentResult>.Ok(new AssignmentResult(snapshot, false, snapshot.AssignedTechnicianUserId, []));

        if (self && AppointmentStatuses.IsConfirmed(snapshot.Status))
            return Fail(SchedulingErrorCodes.AppointmentAlreadyTaken, "Another technician already took this appointment.");
        if (!AppointmentStatuses.IsChangeable(snapshot.Status))
            return Fail(SchedulingErrorCodes.InvalidBookingState, "Cancelled or completed appointments cannot be assigned.");
        if (snapshot.StartsAt <= now)
            return Fail(SchedulingErrorCodes.InvalidBookingState, "Started or past appointments cannot be assigned.");
        if (spec.ExpectedRevision is { } expected && expected != snapshot.Revision)
            return Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed since you loaded it. Reload and try again.");
        if (!self && string.IsNullOrWhiteSpace(spec.Reason))
            return Fail(SchedulingErrorCodes.AssignmentReasonRequired, "A reason is required when staff assign a technician.");
        return null;
    }

    /// <summary>
    /// The target's eligibility for the survey interval (its own pending holds are ignored: they are re-homed; so are
    /// <paramref name="ignore"/>, the appointment itself or the one being replaced) and the holds to re-home: those conflicting
    /// with the interval, plus — if the daily limit would otherwise be hit — further holds of that day, latest first.
    /// </summary>
    private async Task<(HoldRehomePlan? Plan, string? Reason)> PlanIntervalAsync(Guid technicianUserId, DateTimeOffset startsAt, DateTimeOffset endsAt,
        IReadOnlyCollection<Guid>? zoneIds, IReadOnlySet<Guid> ignore, CancellationToken token)
    {
        var date = eligibility.Calendar.LocalDateOf(startsAt);
        var day = (await eligibility.LoadDaysAsync(date, new TechnicianScope([technicianUserId], SchedulableOnly: false), token)).SingleOrDefault();
        if (day is null) return (null, SchedulingErrorCodes.TechnicianInactive);

        var holds = day.Appointments.Where(a => a.IsPendingHold && !ignore.Contains(a.AppointmentId)).ToList();
        var ignored = holds.Select(h => h.AppointmentId).Concat(ignore).ToHashSet();
        var request = new EligibilityRequest(startsAt, endsAt, zoneIds, ignored);
        if (eligibility.Evaluate(day, request) is { } reason) return (null, reason);

        var toMove = holds.Where(h => EligibilityRules.Conflicts(h, startsAt, endsAt, eligibility.Buffer)).ToList();
        var max = day.MaxAppointmentsPerDayOverride ?? eligibility.DefaultMaxPerDay;
        var keep = holds.Except(toMove).OrderByDescending(h => h.StartsAtUtc).ToList();
        var remaining = day.CountedCommitments(toMove.Select(h => h.AppointmentId).Concat(ignore).ToHashSet());
        foreach (var extra in keep)
        {
            if (remaining < max) break;
            toMove.Add(extra);
            remaining--;
        }
        if (remaining >= max) return (null, SchedulingErrorCodes.DailyLimitReached);

        var items = await HoldPlanAsync(toMove.Select(h => h.AppointmentId), token);
        return (new HoldRehomePlan(items, await CandidatesAsync(items, token)), null);
    }

    private async Task<List<HoldPlanItem>> HoldPlanAsync(IEnumerable<Guid> holdIds, CancellationToken token)
    {
        var plan = new List<HoldPlanItem>();
        foreach (var id in holdIds.Distinct())
        {
            if (await unitOfWork.GetAppointmentAsync(id, token) is { } hold)
                plan.Add(new HoldPlanItem(hold, await unitOfWork.GetSiteCoverageAsync(hold.PropertySiteId, token)));
        }
        return plan;
    }

    private async Task<IReadOnlyList<Guid>> CandidatesAsync(IReadOnlyList<HoldPlanItem> plan, CancellationToken token)
    {
        var ids = new HashSet<Guid>();
        foreach (var group in plan.Where(p => p.Coverage.Zone is not null).GroupBy(p => eligibility.Calendar.LocalDateOf(p.Hold.StartsAt)))
        {
            var zones = group.SelectMany(p => p.Coverage.ZoneIds).Distinct().ToList();
            foreach (var day in await eligibility.LoadDaysAsync(group.Key, new TechnicianScope(ZoneIds: zones), token)) ids.Add(day.TechnicianUserId);
        }
        return ids.ToList();
    }

    /// <summary>New holder per hold (null result = some hold cannot be re-homed). Only locked technicians are used.</summary>
    private async Task<Dictionary<Guid, Guid>?> ChooseTargetsAsync(IReadOnlyList<HoldPlanItem> plan, IReadOnlySet<Guid> locked, Guid technicianUserId,
        IReadOnlySet<Guid>? leaving, CancellationToken token)
    {
        var targets = new Dictionary<Guid, Guid>();
        var cache = new Dictionary<DateOnly, List<TechnicianDayState>>();
        foreach (var item in plan.OrderBy(p => p.Hold.StartsAt))
        {
            // Commitments leaving their holders in this transaction (the appointment moving to the technician being
            // confirmed, or the appointment being replaced) free those holders to take over a hold in their place.
            var target = await PickHolderAsync(item.Hold, item.Coverage, locked, technicianUserId, cache, token, leaving);
            if (target is null) return null;
            targets[item.Hold.Id] = target.Value;
        }
        return targets;
    }

    /// <summary>The best eligible holder among locked, schedulable technicians of the hold's zone; the choice is recorded in the cache.</summary>
    private async Task<Guid?> PickHolderAsync(AppointmentSnapshot hold, SiteCoverage coverage, IReadOnlySet<Guid> locked, Guid exclude,
        Dictionary<DateOnly, List<TechnicianDayState>> cache, CancellationToken token, IReadOnlySet<Guid>? leaving = null)
    {
        if (coverage.Zone is null) return null;
        var date = eligibility.Calendar.LocalDateOf(hold.StartsAt);
        var days = await DaysAsync(date, coverage, locked, cache, token, leaving);
        var request = new EligibilityRequest(hold.StartsAt, hold.EndsAt, coverage.ZoneIds, new HashSet<Guid> { hold.Id });
        var best = eligibility.Rank(days.Where(d => d.TechnicianUserId != exclude && locked.Contains(d.TechnicianUserId)), request).FirstOrDefault();
        if (best is null) return null;
        var index = days.FindIndex(d => d.TechnicianUserId == best.Day.TechnicianUserId);
        days[index] = days[index].Without(hold.Id).With(new ScheduledStop(hold.Id, hold.StartsAt, hold.EndsAt, IsPendingHold: true));
        return best.Day.TechnicianUserId;
    }

    /// <summary>Per-date day states of locked schedulable technicians; extended with members of further zones as needed.</summary>
    private async Task<List<TechnicianDayState>> DaysAsync(DateOnly date, SiteCoverage coverage, IReadOnlySet<Guid> locked,
        Dictionary<DateOnly, List<TechnicianDayState>> cache, CancellationToken token, IReadOnlySet<Guid>? leaving = null)
    {
        if (!cache.TryGetValue(date, out var days))
        {
            days = [];
            cache[date] = days;
        }
        var missing = locked.Where(id => days.All(d => d.TechnicianUserId != id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var day in await eligibility.LoadDaysAsync(date, new TechnicianScope(missing, coverage.ZoneIds), token))
            {
                if (days.Any(d => d.TechnicianUserId == day.TechnicianUserId)) continue;
                var state = day;
                foreach (var id in leaving ?? new HashSet<Guid>()) state = state.Without(id);
                days.Add(state);
            }
        }
        return days;
    }

    /// <summary>Step 1: the holds leave the technician (temporarily ESCALATED without a holder, inside the transaction only).</summary>
    private async Task<List<DetachedHold>?> DetachAsync(IReadOnlyList<HoldPlanItem> plan, DateTimeOffset now, CancellationToken token)
    {
        var detached = new List<DetachedHold>();
        foreach (var item in plan)
        {
            var hold = await unitOfWork.FindAppointmentForUpdateAsync(item.Hold.Id, token);
            if (hold is null || hold.Revision != item.Hold.Revision || !AppointmentStatuses.IsOpen(hold.Status)) return null;
            detached.Add(new DetachedHold(hold, hold.Status, hold.ReservedTechnicianUserId));
            hold.ReservedTechnicianUserId = null;
            hold.Status = AppointmentStatuses.Escalated;
            hold.UpdatedAt = now;
            hold.Revision++;
        }
        if (detached.Count > 0) await unitOfWork.SaveChangesAsync(token);
        return detached;
    }

    private static void Confirm(TechnicianAppointment appointment, AssignmentSpec spec, bool self, DateTimeOffset now)
    {
        appointment.ReservedTechnicianUserId = spec.TechnicianUserId;
        appointment.AssignedTechnicianUserId = spec.TechnicianUserId;
        appointment.Status = self ? AppointmentStatuses.Accepted : AppointmentStatuses.Assigned;
        appointment.AssignedAt = now;
        appointment.AcceptedAt = self ? now : null;
        appointment.AssignmentSource = spec.Source;
        appointment.AssignmentReason = self ? null : Trim(spec.Reason, 500);
        appointment.AssignedBySaleUserId = self ? null : spec.ActorUserId;
        appointment.UpdatedAt = now;
        appointment.Revision++;
    }

    private void RecordConfirmation(TechnicianAppointment appointment, AssignmentSpec spec, bool self, Guid? previousAssignee, Guid? previousHolder, Guid? owner, DateTimeOffset now)
    {
        if (self)
        {
            AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Accepted, spec.TechnicianUserId, now,
                previousHolder, spec.TechnicianUserId, notify: [owner, appointment.BookedByUserId]);
            return;
        }
        RecordStaffAssignment(unitOfWork, appointment, spec.Source, spec.Reason, spec.ActorUserId, spec.TechnicianUserId, previousAssignee, previousHolder, owner, now);
    }

    /// <summary>
    /// ASSIGNED (no confirmed technician before) or REASSIGNED (old → new confirmed technician) with the staff source and reason,
    /// notifying the technicians, the Sales owner (unless they acted) and the customer. Shared by staff assignment and Sales reschedule.
    /// </summary>
    internal static void RecordStaffAssignment(ISchedulingUnitOfWork unitOfWork, TechnicianAppointment appointment, string source, string? reason, Guid actorUserId,
        Guid technicianUserId, Guid? previousAssignee, Guid? previousHolder, Guid? owner, DateTimeOffset now)
    {
        var reassigned = previousAssignee is not null && previousAssignee != technicianUserId;
        AppointmentHistory.Record(unitOfWork, appointment, reassigned ? AppointmentEventTypes.Reassigned : AppointmentEventTypes.Assigned, actorUserId, now,
            reassigned ? previousAssignee : previousHolder, technicianUserId, appointment.StartsAt, appointment.StartsAt,
            $"{source}: {Trim(reason, 450)}",
            notify: [technicianUserId, reassigned ? previousAssignee : null, owner == actorUserId ? null : owner, appointment.BookedByUserId]);
    }

    private static SchedulingResult<AssignmentResult> SiteLocationRequired()
        => Fail(SchedulingErrorCodes.SiteLocationRequired,
            "The property site has no valid administrative location. The customer must set the province and the current commune of the site first.");

    private static string ReservationDescribe(string code) => AppointmentReservationService.Describe(code);

    private static SchedulingResult<AssignmentResult> Fail(string code, string message) => SchedulingResult<AssignmentResult>.Fail(code, message);

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    public static AppointmentSnapshot Snapshot(TechnicianAppointment a) => new(
        a.Id, a.AppointmentType, a.SurveyRequestId, a.PropertySiteId, a.OperationalZoneId, a.StartsAt, a.EndsAt, a.ReservedTechnicianUserId,
        a.AssignedTechnicianUserId, a.AssignedBySaleUserId, a.Status, a.CustomerNote, a.BookedByUserId, a.BookedAt, a.AcceptanceDeadlineAt,
        a.EscalatedAt, a.AcceptedAt, a.AssignedAt, a.AssignmentSource, a.AssignmentReason, a.CompletedAt, a.Revision);
}
