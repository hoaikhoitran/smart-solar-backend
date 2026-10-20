using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.Scheduling.Dispatch;

public sealed record PreferredWindowInput(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record CreateDispatchRequestCommand(Guid CustomerUserId, Guid SurveyRequestId, IReadOnlyList<PreferredWindowInput> PreferredWindows, string? Note);

/// <summary>Direct booking (assign) or a proposal. <see cref="Reason"/>: required for an assignment and for any cross-zone technician.</summary>
public sealed record DispatchTechnicianCommand(Guid ActorUserId, Guid DispatchRequestId, Guid TechnicianUserId, DateTimeOffset StartsAt, bool CrossZoneAuthorized,
    string? Reason, int ExpectedRevision, string? Note = null);

public sealed record ResolveDispatchCommand(Guid ActorUserId, Guid DispatchRequestId, string? Reason, int? ExpectedRevision);

public sealed record WithdrawProposalCommand(Guid ActorUserId, Guid DispatchRequestId, Guid ProposalId, string? Reason);

public sealed record RespondToProposalCommand(Guid CustomerUserId, Guid SurveyRequestId, Guid ProposalId, int? ExpectedRevision, string? Reason);

public sealed record CancelDispatchCommand(Guid CustomerUserId, Guid SurveyRequestId, string? Reason);

/// <summary>
/// Uncovered-zone dispatch: customers ask for a technician when slots cannot serve them; Manager/Admin either book a technician
/// directly (ASSIGNED, source MANAGER_DISPATCH) or send a proposal the customer accepts or declines.
/// <list type="bullet">
/// <item>Never a confirmed appointment without valid capacity: every booking goes through
/// <see cref="AppointmentAssignmentService.ReserveForTechnicianAsync"/> (technician → site → zone locks, eligibility re-check under
/// the locks, hold re-homing, exclusion constraint); the dispatch_request row is locked after those.</item>
/// <item>Cross-zone is manual only: a technician outside the site's zone (or any technician when the commune has no zone) needs
/// crossZoneAuthorized = true and a reason, recorded as a cross_zone_authorization row; it bypasses only the zone rule.</item>
/// <item>Proposals reserve nothing; acceptance re-verifies everything and books atomically, or marks the proposal FAILED and the
/// request OPEN again.</item>
/// <item>Timing (review SLA, escalation, reminders, proposal window) is snapshotted from the settings current when the request or
/// proposal is created. Requests never expire.</item>
/// </list>
/// </summary>
public sealed class DispatchService(
    ISchedulingUnitOfWork scheduling,
    IDispatchUnitOfWork dispatch,
    AppointmentAssignmentService assignments,
    ZoneCoverageService coverage,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public const int MaxPreferredWindows = 5;
    public const int MaxWindowHours = 24;
    public const int MaxReasonLength = 500;
    public const int MaxNoteLength = 1000;

    /// <summary>Internal abort code: the proposal was already accepted (an idempotent repeat of the acceptance).</summary>
    private const string AlreadyAccepted = "DISPATCH_PROPOSAL_ALREADY_ACCEPTED";

    private TechnicianEligibility Eligibility => assignments.Eligibility;

    // ================================================================== customer

    /// <summary>
    /// Creates the request (201) or returns the open one (200, idempotent). Every temporarily uncovered in-scope site is accepted,
    /// including a zone marked not serviceable; refused only for SITE_LOCATION_REQUIRED (422: invalid / out-of-scope location) and
    /// when the site is bookable at the wanted times (409 DISPATCH_NOT_NEEDED).
    /// </summary>
    public async Task<SchedulingResult<CustomerDispatchView>> CreateAsync(CreateDispatchRequestCommand command, CancellationToken cancellationToken)
    {
        var context = await scheduling.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId) return SurveyNotFound<CustomerDispatchView>();

        if (await dispatch.FindActiveForSurveyRequestAsync(command.SurveyRequestId, cancellationToken) is { } existing)
            return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(existing, created: false, cancellationToken));

        // An open appointment whose start passed is dead: cancel it now (as the worker would) instead of blocking the request.
        if (context.ActiveAppointment is { } stale && AppointmentStatuses.IsOpen(stale.Status) && stale.StartsAt <= time.GetUtcNow())
        {
            await AppointmentEscalationService.TryCancelNotAcceptedAsync(scheduling, time, stale.Id, cancellationToken);
            context = await scheduling.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
            if (context is null) return SurveyNotFound<CustomerDispatchView>();
        }
        if (context.ActiveAppointment is not null)
            return SchedulingResult<CustomerDispatchView>.Fail(SchedulingErrorCodes.BookingAlreadyExists,
                "This survey request already has an active appointment. Reschedule or cancel it instead.");
        if (context.Status != SurveyRequestStatus.Pending)
            return SchedulingResult<CustomerDispatchView>.Fail(SchedulingErrorCodes.InvalidBookingState, "This survey request can no longer be scheduled.");

        var policy = await Eligibility.PolicyAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (ValidateWindows(command.PreferredWindows, now, policy) is { } invalid)
            return SchedulingResult<CustomerDispatchView>.Fail(SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.", invalid);
        if (command.Note is { Length: > MaxNoteLength })
            return SchedulingResult<CustomerDispatchView>.Fail(SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["note"] = [$"'note' must be at most {MaxNoteLength} characters."] });

        var evaluation = await coverage.EvaluateAsync(context.PropertySiteId, cancellationToken: cancellationToken);
        string reason;
        if (evaluation.Status == CoverageStatuses.Covered)
        {
            if (command.PreferredWindows.Count == 0 || await AnyBookableSlotAsync(context.PropertySiteId, command.PreferredWindows, policy, now, cancellationToken))
                return SchedulingResult<CustomerDispatchView>.Fail(DispatchErrorCodes.DispatchNotNeeded,
                    "Technicians of your area are available at the times you want: book one of the listed slots instead.",
                    CapacityFailures.Details(CoverageStatuses.Covered));
            reason = CoverageStatuses.ScheduleConflict;
        }
        else if (CoverageStatuses.IsDispatchable(evaluation.Status))
        {
            reason = evaluation.Status;
        }
        else
        {
            return CapacityFailures.Fail<CustomerDispatchView>(evaluation.Status, CoverageMessages.Describe(evaluation.Status)!);
        }

        DispatchRequest? created;
        try
        {
            created = await scheduling.ExecuteInTransactionAsync<DispatchRequest?>(async token =>
            {
                // Same site lock as a booking: a booking and a dispatch request of the site cannot interleave.
                await scheduling.LockPropertySiteAsync(context.PropertySiteId, token);
                if (await dispatch.FindActiveForSurveyRequestAsync(command.SurveyRequestId, token) is not null) return (null, false);
                var fresh = await scheduling.GetSurveyRequestContextAsync(command.SurveyRequestId, token);
                if (fresh is null || fresh.ActiveAppointment is not null || fresh.Status != SurveyRequestStatus.Pending) return (null, false);

                var at = time.GetUtcNow();
                var request = DispatchRequests.Open(command.SurveyRequestId, context.PropertySiteId, command.CustomerUserId, evaluation.Site.Zone?.Id,
                    reason, string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(), policy, at);
                var reviewDue = request.ReviewDueAt;
                dispatch.Add(request);
                foreach (var w in command.PreferredWindows.OrderBy(w => w.StartsAt))
                    dispatch.Add(new DispatchPreferredWindow { Id = Guid.NewGuid(), DispatchRequestId = request.Id, StartsAt = w.StartsAt.ToUniversalTime(), EndsAt = w.EndsAt.ToUniversalTime() });
                await RecordAsync(request, DispatchEventTypes.Created, command.CustomerUserId, at,
                    $"{reason}; review due {reviewDue:O} (SLA {policy.DispatchReviewSlaMinutes} min, settings v{policy.Version})", null,
                    await StaffRecipientsAsync(request, token), token);
                await dispatch.SaveChangesAsync(token);
                return (request, true);
            }, cancellationToken);
        }
        catch (DispatchConflictException ex) when (ex.Kind == DispatchConflictException.ActiveRequestExists)
        {
            created = null;   // a concurrent identical request won: return it
        }

        if (created is not null) return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(created, created: true, cancellationToken));
        if (await dispatch.FindActiveForSurveyRequestAsync(command.SurveyRequestId, cancellationToken) is { } winner)
            return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(winner, created: false, cancellationToken));
        return SchedulingResult<CustomerDispatchView>.Fail(SchedulingErrorCodes.BookingAlreadyExists,
            "This survey request changed meanwhile (it was booked or is no longer pending). Reload and try again.");
    }

    /// <summary>The newest dispatch request of the customer's survey request, with its live proposal.</summary>
    public async Task<SchedulingResult<CustomerDispatchView>> GetForCustomerAsync(Guid customerUserId, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var request = await OwnedLatestAsync(customerUserId, surveyRequestId, cancellationToken);
        return request is null ? DispatchNotFound<CustomerDispatchView>() : SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(request, false, cancellationToken));
    }

    /// <summary>
    /// Accepts a live proposal: still PROPOSED, not expired, starts in the future; eligibility, capacity and conflicts are re-checked
    /// under the scheduling locks and the appointment is booked ASSIGNED (MANAGER_DISPATCH) atomically with the proposal (ACCEPTED)
    /// and the request (ASSIGNED). When the technician can no longer take it the proposal becomes FAILED (precise code) and the request
    /// OPEN again. A repeat of a successful acceptance returns 200 unchanged.
    /// </summary>
    public async Task<SchedulingResult<CustomerDispatchView>> AcceptAsync(RespondToProposalCommand command, CancellationToken cancellationToken)
    {
        var (request, proposal) = await OwnedProposalAsync(command.CustomerUserId, command.SurveyRequestId, command.ProposalId, cancellationToken);
        if (request is null || proposal is null) return ProposalNotFound<CustomerDispatchView>();
        if (proposal.Status == ProposalStatuses.Accepted) return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(request, false, cancellationToken));
        if (ProposalStateError<CustomerDispatchView>(proposal) is { } state) return state;
        if (command.ExpectedRevision is { } expected && expected != proposal.Revision)
            return SchedulingResult<CustomerDispatchView>.Fail(DispatchErrorCodes.StaleRevision, "The proposal changed meanwhile. Reload and try again.",
                new { currentRevision = proposal.Revision });
        if (proposal.ExpiresAt <= time.GetUtcNow())
        {
            await new DispatchSweepService(scheduling, dispatch, time).TryExpireProposalAsync(proposal.Id, cancellationToken);
            return ProposalExpiredFailure<CustomerDispatchView>();
        }

        var spec = new ReservationSpec(proposal.StartsAt, proposal.EndsAt, request.PropertySiteId, proposal.TechnicianUserId, new HashSet<Guid>(), []);
        // Current booking limits (minimum lead, horizon): a proposal reserves nothing, so acceptance re-checks the start like a booking.
        var policy = await Eligibility.PolicyAsync(cancellationToken);
        ReservationOutcome outcome;
        try
        {
            outcome = await assignments.ReserveForTechnicianAsync(spec, command.CustomerUserId, async (ctx, token) =>
            {
                var locked = await dispatch.LockAsync(request.Id, token);
                var live = await dispatch.FindProposalForUpdateAsync(proposal.Id, token);
                if (locked is null || live is null) return ApplyResult.Abort(DispatchErrorCodes.ProposalNotFound, "The proposal was not found.");
                if (live.Status == ProposalStatuses.Accepted) return ApplyResult.Abort(AlreadyAccepted, "Already accepted.");
                if (live.Status == ProposalStatuses.Expired || (live.Status == ProposalStatuses.Proposed && live.ExpiresAt <= ctx.NowUtc))
                    return ApplyResult.Abort(DispatchErrorCodes.ProposalExpired, ProposalExpiredMessage);
                if (live.Status != ProposalStatuses.Proposed)
                    return ApplyResult.Abort(DispatchErrorCodes.ProposalNotPending, $"This proposal is {live.Status} and can no longer be accepted.");
                if (locked.Status != DispatchStatuses.ProposalPending)
                    return ApplyResult.Abort(DispatchErrorCodes.InvalidState, $"The dispatch request is {locked.Status}.");
                if (!calendar.IsBookableStart(live.StartsAt, ctx.NowUtc, policy.Booking))
                    return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingTime, StartMessage(policy));

                var booked = await BookAsync(locked, ctx, live.TechnicianUserId, live.StartsAt, live.EndsAt, live.ProposedByUserId,
                    $"DISPATCH_PROPOSAL_ACCEPTED: proposal {live.Id}", live.CrossZoneAuthorizationId, command.CustomerUserId, token);
                if (!booked.Succeeded) return booked;

                live.Status = ProposalStatuses.Accepted;
                live.RespondedAt = ctx.NowUtc;
                live.Revision++;
                Resolve(locked, DispatchStatuses.Assigned, DispatchResolutions.Assigned, command.CustomerUserId, "Customer accepted the proposal.", ctx.NowUtc);
                locked.AppointmentId = booked.AppointmentId;
                await RecordAsync(locked, DispatchEventTypes.ProposalAccepted, command.CustomerUserId, ctx.NowUtc, $"appointment {booked.AppointmentId}", live.Id,
                    [live.ProposedByUserId], token);
                return booked;
            }, cancellationToken, crossZone: proposal.CrossZone);
        }
        catch (SchedulingConflictException)
        {
            outcome = ReservationOutcome.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The schedule changed meanwhile. Please try again.");
        }

        // A concurrent acceptance of the same proposal (double click, retry) may have won while this one was planning: its booking now
        // blocks this one, which is the same outcome — report success unchanged instead of a conflict.
        if (outcome.Succeeded || outcome.ErrorCode == AlreadyAccepted
            || (await dispatch.FindProposalAsync(proposal.Id, cancellationToken))?.Status == ProposalStatuses.Accepted)
            return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync((await dispatch.GetAsync(request.Id, cancellationToken))!, false, cancellationToken));
        if (outcome.ErrorCode == DispatchErrorCodes.ProposalExpired)
        {
            await new DispatchSweepService(scheduling, dispatch, time).TryExpireProposalAsync(proposal.Id, cancellationToken);
            return ProposalExpiredFailure<CustomerDispatchView>();
        }
        // Transient contention or the proposal/request changed state: nothing to mark, report as is.
        if (outcome.ErrorCode is SchedulingErrorCodes.ConcurrencyConflict or DispatchErrorCodes.ProposalNotPending or DispatchErrorCodes.InvalidState
            or DispatchErrorCodes.ProposalNotFound)
            return SchedulingResult<CustomerDispatchView>.Fail(outcome.ErrorCode!, outcome.Message!);

        // The technician / time is no longer valid (eligibility, capacity, conflict, coverage, the request got another appointment):
        // the proposal fails and the request is actionable again for another proposal.
        var failed = await MarkProposalFailedAsync(request.Id, proposal.Id, outcome.ErrorCode!, outcome.Message!, cancellationToken);
        var details = new Dictionary<string, object?>
        {
            ["proposalStatus"] = failed ? ProposalStatuses.Failed : null,
            ["dispatchStatus"] = failed ? DispatchStatuses.Open : null,
        };
        if (CapacityFailures.IsCoverageCode(outcome.ErrorCode))
        {
            details["coverageStatus"] = outcome.ErrorCode;
            details["dispatchAvailable"] = CoverageStatuses.IsDispatchable(outcome.ErrorCode!);
        }
        return SchedulingResult<CustomerDispatchView>.Fail(outcome.ErrorCode!,
            outcome.Message! + " The proposal can no longer be accepted; our staff will send you another one.", details);
    }

    /// <summary>PROPOSED → DECLINED; the request is OPEN again for another proposal. A repeat returns 200 unchanged.</summary>
    public async Task<SchedulingResult<CustomerDispatchView>> DeclineAsync(RespondToProposalCommand command, CancellationToken cancellationToken)
    {
        var (request, proposal) = await OwnedProposalAsync(command.CustomerUserId, command.SurveyRequestId, command.ProposalId, cancellationToken);
        if (request is null || proposal is null) return ProposalNotFound<CustomerDispatchView>();
        if (command.Reason is { Length: > MaxReasonLength })
            return Invalid<CustomerDispatchView>("reason", $"'reason' must be at most {MaxReasonLength} characters.");
        var reason = Trim(command.Reason, MaxReasonLength);
        var result = await CloseProposalAsync(request.Id, proposal.Id, ProposalStatuses.Declined, DispatchEventTypes.ProposalDeclined, command.CustomerUserId,
            reason, command.ExpectedRevision, notifyProposer: true, cancellationToken);
        return result.Succeeded
            ? SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync((await dispatch.GetAsync(request.Id, cancellationToken))!, false, cancellationToken))
            : SchedulingResult<CustomerDispatchView>.Fail(result.ErrorCode!, result.Message!, result.Details);
    }

    /// <summary>The customer withdraws the request: CLOSED / CANCELLED_BY_CUSTOMER (a live proposal is withdrawn). A repeat returns 200.</summary>
    public async Task<SchedulingResult<CustomerDispatchView>> CancelAsync(CancelDispatchCommand command, CancellationToken cancellationToken)
    {
        var latest = await OwnedLatestAsync(command.CustomerUserId, command.SurveyRequestId, cancellationToken);
        if (latest is null) return DispatchNotFound<CustomerDispatchView>();
        if (command.Reason is { Length: > MaxReasonLength }) return Invalid<CustomerDispatchView>("reason", $"'reason' must be at most {MaxReasonLength} characters.");
        if (latest.Status == DispatchStatuses.Closed && latest.Resolution == DispatchResolutions.CancelledByCustomer)
            return SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync(latest, false, cancellationToken));
        var result = await ResolveAsync(latest.Id, DispatchStatuses.Closed, DispatchResolutions.CancelledByCustomer, DispatchEventTypes.CancelledByCustomer,
            command.CustomerUserId, Trim(command.Reason, MaxReasonLength) ?? "Cancelled by the customer.", null, notifyCustomer: false, cancellationToken);
        return result.Succeeded
            ? SchedulingResult<CustomerDispatchView>.Ok(await CustomerViewAsync((await dispatch.GetAsync(latest.Id, cancellationToken))!, false, cancellationToken))
            : SchedulingResult<CustomerDispatchView>.Fail(result.ErrorCode!, result.Message!, result.Details);
    }

    // ================================================================== Manager / Admin

    public async Task<DispatchQueuePage> ListAsync(string? status, string? reason, bool? overdue, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 10_000); // bounded: (page - 1) * pageSize must not overflow into a negative OFFSET
        pageSize = Math.Clamp(pageSize, 1, 100);
        var now = time.GetUtcNow();
        var (rows, total) = await dispatch.ListQueueAsync(new DispatchQueueFilter(status, reason, overdue, (page - 1) * pageSize, pageSize), now, cancellationToken);
        var names = await NamesAsync(rows.Select(r => r.LiveProposal?.TechnicianUserId), cancellationToken);
        return new DispatchQueuePage(rows.Select(r => Item(r, now, names)).ToList(), page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<SchedulingResult<DispatchStaffView>> GetAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
    {
        var row = await dispatch.GetRowAsync(dispatchRequestId, cancellationToken);
        return row is null ? DispatchNotFound<DispatchStaffView>() : SchedulingResult<DispatchStaffView>.Ok(await StaffViewAsync(row, cancellationToken));
    }

    /// <summary>
    /// Technicians for [startsAt, startsAt + appointment duration): zone members first, then every other technician flagged crossZone,
    /// each with the eligibility reason (zone rule skipped for cross-zone ones) and known schedule facts. Nothing is reserved.
    /// </summary>
    public async Task<SchedulingResult<DispatchCandidatesView>> CandidatesAsync(Guid dispatchRequestId, DateTimeOffset startsAt, CancellationToken cancellationToken)
    {
        var request = await dispatch.GetAsync(dispatchRequestId, cancellationToken);
        if (request is null) return DispatchNotFound<DispatchCandidatesView>();
        var policy = await Eligibility.PolicyAsync(cancellationToken);
        var start = startsAt.ToUniversalTime();
        if (!calendar.IsBookableStart(start, time.GetUtcNow(), policy.Booking))
            return SchedulingResult<DispatchCandidatesView>.Fail(SchedulingErrorCodes.InvalidBookingTime, StartMessage(policy));
        var end = start + calendar.Duration;

        var site = await scheduling.GetSiteCoverageAsync(request.PropertySiteId, cancellationToken);
        var technicians = await scheduling.ListTechniciansAsync(null, cancellationToken);
        var date = calendar.LocalDateOf(start);
        var days = (await Eligibility.LoadDaysAsync(date, new TechnicianScope(technicians.Select(t => t.UserId).ToList(), SchedulableOnly: false), cancellationToken))
            .ToDictionary(d => d.TechnicianUserId);
        var zoneIds = site.ZoneIds;

        var candidates = new List<DispatchCandidateView>();
        foreach (var technician in technicians)
        {
            // Same rule as the assignment: in a zone marked not serviceable every technician needs the explicit (cross-zone) approval.
            var member = site.Zone is { IsServiceable: true } zone && technician.Zones.Any(z => z.Id == zone.Id);
            var zones = technician.Zones.Select(z => new ZoneView(z.Id, z.Code, z.Name)).ToList();
            if (!days.TryGetValue(technician.UserId, out var day))
            {
                candidates.Add(new DispatchCandidateView(technician.UserId, technician.FullName, !member, false, EligibilityReasons.TechnicianInactive, false, zones, 0,
                    technician.Profile?.MaxAppointmentsPerDay ?? Eligibility.DefaultMaxPerDay, []));
                continue;
            }
            // Own pending holds are handed to colleagues on assignment, so they are not obstacles (flagged instead).
            var holds = day.Appointments.Where(a => a.IsPendingHold).Select(a => a.AppointmentId).ToHashSet();
            var restriction = member ? zoneIds : null;
            var lenient = Eligibility.Evaluate(day, new EligibilityRequest(start, end, restriction, holds));
            var strict = lenient is null ? Eligibility.Evaluate(day, new EligibilityRequest(start, end, restriction)) : null;
            candidates.Add(new DispatchCandidateView(technician.UserId, technician.FullName, !member, lenient is null, lenient, lenient is null && strict is not null,
                zones, day.CountedCommitments(), day.MaxAppointmentsPerDayOverride ?? Eligibility.DefaultMaxPerDay,
                (day.WorkingIntervals ?? []).Select(w => new WorkingIntervalView(Local(w.StartsAtUtc), Local(w.EndsAtUtc))).ToList()));
        }

        var ordered = candidates.OrderBy(c => c.CrossZone).ThenByDescending(c => c.IsEligible).ThenBy(c => c.FullName, StringComparer.Ordinal)
            .ThenBy(c => c.TechnicianUserId).ToList();
        return SchedulingResult<DispatchCandidatesView>.Ok(new DispatchCandidatesView(request.Id, Local(start), Local(end), ZoneView.From(site.Zone), site.Status, ordered,
            "Known schedule facts only: working hours, leave, commitments and the fixed buffer. No travel time or distance is computed. "
            + "Cross-zone technicians need crossZoneAuthorized = true and a reason."));
    }

    /// <summary>
    /// Books the technician directly as ASSIGNED (MANAGER_DISPATCH) through the shared reservation path, the survey request becomes
    /// Scheduled and the request ASSIGNED. Only an OPEN request (no live proposal) can be assigned; the reason is required.
    /// </summary>
    public async Task<SchedulingResult<DispatchStaffView>> AssignAsync(DispatchTechnicianCommand command, CancellationToken cancellationToken)
    {
        var reason = Trim(command.Reason, MaxReasonLength);
        if (reason is null) return SchedulingResult<DispatchStaffView>.Fail(SchedulingErrorCodes.AssignmentReasonRequired, "A reason is required to assign a technician.");
        var (request, window, crossZone, error) = await PrepareAsync(command, reason, cancellationToken);
        if (error is not null) return error.As<DispatchStaffView>();

        ReservationOutcome outcome;
        try
        {
            outcome = await assignments.ReserveForTechnicianAsync(new ReservationSpec(window.Start, window.End, request!.PropertySiteId, command.TechnicianUserId,
                new HashSet<Guid>(), []), command.ActorUserId, async (ctx, token) =>
            {
                var locked = await dispatch.LockAsync(request.Id, token);
                if (locked is null) return ApplyResult.Abort(DispatchErrorCodes.DispatchRequestNotFound, "Dispatch request was not found.");
                if (StateAbort(locked, command.ExpectedRevision) is { } abort) return abort;
                Guid? authorization = crossZone ? Authorize(locked, null, command.TechnicianUserId, ctx.Coverage.Zone?.Id, reason, command.ActorUserId, ctx.NowUtc) : null;
                var booked = await BookAsync(locked, ctx, command.TechnicianUserId, window.Start, window.End, command.ActorUserId, reason, authorization,
                    command.ActorUserId, token);
                if (!booked.Succeeded) return booked;
                if (authorization is { } id)
                    await RecordAsync(locked, DispatchEventTypes.CrossZoneAuthorized, command.ActorUserId, ctx.NowUtc, $"technician {command.TechnicianUserId}; authorization {id}; {reason}",
                        null, [], token);
                Resolve(locked, DispatchStatuses.Assigned, DispatchResolutions.Assigned, command.ActorUserId, reason, ctx.NowUtc);
                locked.AppointmentId = booked.AppointmentId;
                await RecordAsync(locked, DispatchEventTypes.Assigned, command.ActorUserId, ctx.NowUtc,
                    $"technician {command.TechnicianUserId}; {window.Start:O}; appointment {booked.AppointmentId}", null, [], token);
                return booked;
            }, cancellationToken, crossZone);
        }
        catch (SchedulingConflictException)
        {
            outcome = ReservationOutcome.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The schedule changed meanwhile. Reload and try again.");
        }
        if (!outcome.Succeeded) return CapacityFailures.Fail<DispatchStaffView>(outcome.ErrorCode!, outcome.Message!);
        return await GetAsync(request!.Id, cancellationToken);
    }

    /// <summary>
    /// Sends a proposal: eligibility is validated NOW (nothing is reserved), expires_at = LEAST(now + proposal window, starts_at) from the
    /// current settings, and the request becomes PROPOSAL_PENDING. Only one live proposal at a time (409 PROPOSAL_ALREADY_PENDING).
    /// </summary>
    public async Task<SchedulingResult<DispatchStaffView>> ProposeAsync(DispatchTechnicianCommand command, CancellationToken cancellationToken)
    {
        var reason = Trim(command.Reason, MaxReasonLength);
        if (command.Note is { Length: > MaxReasonLength }) return Invalid<DispatchStaffView>("note", $"'note' must be at most {MaxReasonLength} characters.");
        var (request, window, crossZone, error) = await PrepareAsync(command, reason, cancellationToken);
        if (error is not null) return error.As<DispatchStaffView>();

        var site = await scheduling.GetSiteCoverageAsync(request!.PropertySiteId, cancellationToken);
        var (ineligible, _) = await assignments.EvaluateConfirmationAsync(command.TechnicianUserId, window.Start, window.End, crossZone ? null : site.ZoneIds, cancellationToken);
        if (ineligible is not null) return SchedulingResult<DispatchStaffView>.Fail(ineligible, AppointmentReservationService.Describe(ineligible));
        var policy = await Eligibility.PolicyAsync(cancellationToken);

        SchedulingResult<bool> result;
        try
        {
            result = await scheduling.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
            {
                var locked = await dispatch.LockAsync(request.Id, token);
                if (locked is null) return (DispatchNotFound<bool>(), false);
                if (StateAbort(locked, command.ExpectedRevision) is { } abort) return (SchedulingResult<bool>.Fail(abort.ErrorCode!, abort.Message!), false);
                var now = time.GetUtcNow();
                if (window.Start <= now) return (SchedulingResult<bool>.Fail(SchedulingErrorCodes.InvalidBookingTime, StartMessage(policy)), false);

                Guid? authorization = crossZone ? Authorize(locked, null, command.TechnicianUserId, site.Zone?.Id, reason!, command.ActorUserId, now) : null;
                var byWindow = now.AddMinutes(policy.DispatchProposalResponseWindowMinutes);
                var proposal = new DispatchProposal
                {
                    Id = Guid.NewGuid(),
                    DispatchRequestId = locked.Id,
                    TechnicianUserId = command.TechnicianUserId,
                    StartsAt = window.Start,
                    EndsAt = window.End,
                    CrossZone = crossZone,
                    CrossZoneAuthorizationId = authorization,
                    ProposedByUserId = command.ActorUserId,
                    Note = Trim(command.Note, MaxReasonLength),
                    CreatedAt = now,
                    ResponseWindowMinutes = policy.DispatchProposalResponseWindowMinutes,
                    SettingsVersion = policy.Version,
                    ExpiresAt = byWindow < window.Start ? byWindow : window.Start,
                    Status = ProposalStatuses.Proposed,
                };
                dispatch.Add(proposal);
                locked.Status = DispatchStatuses.ProposalPending;
                locked.UpdatedAt = now;
                locked.Revision++;
                if (authorization is { } id)
                    await RecordAsync(locked, DispatchEventTypes.CrossZoneAuthorized, command.ActorUserId, now, $"technician {command.TechnicianUserId}; authorization {id}; {reason}",
                        proposal.Id, [], token);
                await RecordAsync(locked, DispatchEventTypes.ProposalCreated, command.ActorUserId, now,
                    $"technician {command.TechnicianUserId}; {window.Start:O}; expires {proposal.ExpiresAt:O} (window {proposal.ResponseWindowMinutes} min, settings v{policy.Version})",
                    proposal.Id, [locked.CustomerUserId], token);
                await dispatch.SaveChangesAsync(token);
                return (SchedulingResult<bool>.Ok(true), true);
            }, cancellationToken);
        }
        catch (DispatchConflictException ex) when (ex.Kind == DispatchConflictException.ProposalExists)
        {
            result = SchedulingResult<bool>.Fail(DispatchErrorCodes.ProposalAlreadyPending, "A proposal is already waiting for the customer's answer.");
        }
        catch (DispatchConflictException)
        {
            result = Stale<bool>(null);
        }
        return result.Succeeded ? await GetAsync(request.Id, cancellationToken) : result.As<DispatchStaffView>();
    }

    public async Task<SchedulingResult<DispatchStaffView>> WithdrawProposalAsync(WithdrawProposalCommand command, CancellationToken cancellationToken)
    {
        if (command.Reason is { Length: > MaxReasonLength }) return Invalid<DispatchStaffView>("reason", $"'reason' must be at most {MaxReasonLength} characters.");
        var proposal = (await dispatch.ListProposalsAsync(command.DispatchRequestId, cancellationToken)).FirstOrDefault(p => p.Id == command.ProposalId);
        if (proposal is null) return ProposalNotFound<DispatchStaffView>();
        var result = await CloseProposalAsync(command.DispatchRequestId, proposal.Id, ProposalStatuses.Withdrawn, DispatchEventTypes.ProposalWithdrawn, command.ActorUserId,
            Trim(command.Reason, MaxReasonLength), null, notifyProposer: false, cancellationToken);
        return result.Succeeded ? await GetAsync(command.DispatchRequestId, cancellationToken) : result.As<DispatchStaffView>();
    }

    /// <summary>OPEN | PROPOSAL_PENDING → UNSERVICEABLE (reason required; a live proposal is withdrawn).</summary>
    public Task<SchedulingResult<DispatchStaffView>> MarkUnserviceableAsync(ResolveDispatchCommand command, CancellationToken cancellationToken)
        => StaffResolveAsync(command, DispatchStatuses.Unserviceable, DispatchResolutions.Unserviceable, DispatchEventTypes.Unserviceable, cancellationToken);

    /// <summary>OPEN | PROPOSAL_PENDING → CLOSED / CLOSED_BY_STAFF (reason required; a live proposal is withdrawn).</summary>
    public Task<SchedulingResult<DispatchStaffView>> CloseAsync(ResolveDispatchCommand command, CancellationToken cancellationToken)
        => StaffResolveAsync(command, DispatchStatuses.Closed, DispatchResolutions.ClosedByStaff, DispatchEventTypes.Closed, cancellationToken);

    private async Task<SchedulingResult<DispatchStaffView>> StaffResolveAsync(ResolveDispatchCommand command, string status, string resolution, string eventType,
        CancellationToken cancellationToken)
    {
        var reason = Trim(command.Reason, MaxReasonLength);
        if (reason is null) return SchedulingResult<DispatchStaffView>.Fail(DispatchErrorCodes.ReasonRequired, "A reason is required.");
        if (command.Reason!.Trim().Length > MaxReasonLength) return Invalid<DispatchStaffView>("reason", $"'reason' must be at most {MaxReasonLength} characters.");
        var result = await ResolveAsync(command.DispatchRequestId, status, resolution, eventType, command.ActorUserId, reason, command.ExpectedRevision,
            notifyCustomer: true, cancellationToken);
        return result.Succeeded ? await GetAsync(command.DispatchRequestId, cancellationToken) : result.As<DispatchStaffView>();
    }

    // ================================================================== shared steps

    /// <summary>Checks shared by assign and propose (outside any transaction; everything is re-checked in it).</summary>
    private async Task<(DispatchRequest? Request, (DateTimeOffset Start, DateTimeOffset End) Window, bool CrossZone, SchedulingResult<bool>? Error)> PrepareAsync(
        DispatchTechnicianCommand command, string? reason, CancellationToken cancellationToken)
    {
        var request = await dispatch.GetAsync(command.DispatchRequestId, cancellationToken);
        if (request is null) return (null, default, false, DispatchNotFound<bool>());
        if (StateAbort(request, command.ExpectedRevision) is { } abort)
            return (null, default, false, SchedulingResult<bool>.Fail(abort.ErrorCode!, abort.Message!, abort.ErrorCode == DispatchErrorCodes.StaleRevision
                ? new { currentRevision = request.Revision } : null));

        var policy = await Eligibility.PolicyAsync(cancellationToken);
        var start = command.StartsAt.ToUniversalTime();
        if (!calendar.IsBookableStart(start, time.GetUtcNow(), policy.Booking))
            return (null, default, false, SchedulingResult<bool>.Fail(SchedulingErrorCodes.InvalidBookingTime, StartMessage(policy)));

        var technician = await scheduling.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole) return (null, default, false, SchedulingResult<bool>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found."));

        var site = await scheduling.GetSiteCoverageAsync(request.PropertySiteId, cancellationToken);
        if (site.Status is CoverageStatuses.SiteLocationRequired)
            return (null, default, false, CapacityFailures.Fail<bool>(site.Status, CoverageMessages.Describe(site.Status)!));
        // A zone marked not serviceable takes no work from its own members either: every technician needs the explicit authorization.
        var member = site.Zone is { IsServiceable: true } zone
                     && (await scheduling.ListTechnicianZonesAsync(command.TechnicianUserId, cancellationToken)).Any(z => z.Id == zone.Id);
        if (!member && (!command.CrossZoneAuthorized || reason is null))
            return (null, default, false, SchedulingResult<bool>.Fail(DispatchErrorCodes.CrossZoneAuthorizationRequired,
                site.Zone is null
                    ? "The site's commune has no active operational zone, so every technician is cross-zone: set crossZoneAuthorized = true and give a reason."
                    : !site.Zone.IsServiceable
                        ? "The site's operational zone is temporarily not serviceable, so every assignment needs explicit approval: set crossZoneAuthorized = true and give a reason."
                        : "The technician is not a member of the site's operational zone: set crossZoneAuthorized = true and give a reason.",
                new { technicianUserId = command.TechnicianUserId, siteZone = ZoneView.From(site.Zone) }));
        return (request, (start, start + calendar.Duration), !member, null);
    }

    /// <summary>Only an OPEN request at the expected revision can get an assignment or a proposal.</summary>
    private static ApplyResult? StateAbort(DispatchRequest request, int expectedRevision)
    {
        if (request.Status == DispatchStatuses.ProposalPending)
            return ApplyResult.Abort(DispatchErrorCodes.ProposalAlreadyPending, "A proposal is waiting for the customer's answer: withdraw it first.");
        if (request.Status != DispatchStatuses.Open)
            return ApplyResult.Abort(DispatchErrorCodes.InvalidState, $"The dispatch request is {request.Status}.");
        if (request.Revision != expectedRevision)
            return ApplyResult.Abort(DispatchErrorCodes.StaleRevision, "The dispatch request changed meanwhile. Reload and try again.");
        return null;
    }

    /// <summary>
    /// Inside the reservation transaction (technician, site and zone locked, dispatch request locked): the survey request must still
    /// be Pending without an active appointment; writes the ASSIGNED appointment (MANAGER_DISPATCH), schedules the request and records
    /// the BOOKED and ASSIGNED appointment events (technician, Sales owner and customer notified).
    /// </summary>
    private async Task<ApplyResult> BookAsync(DispatchRequest request, ReservationContext ctx, Guid technicianUserId, DateTimeOffset start, DateTimeOffset end,
        Guid assignedBy, string reason, Guid? authorizationId, Guid actorUserId, CancellationToken token)
    {
        var survey = await scheduling.GetSurveyRequestContextAsync(request.SurveyRequestId, token);
        if (survey is null) return ApplyResult.Abort(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (survey.ActiveAppointment is not null)
            return ApplyResult.Abort(SchedulingErrorCodes.BookingAlreadyExists, "The survey request already has an active appointment.");
        if (survey.Status != SurveyRequestStatus.Pending)
            return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "The survey request can no longer be scheduled.");

        var now = ctx.NowUtc;
        var appointment = new TechnicianAppointment
        {
            Id = Guid.NewGuid(),
            AppointmentType = AppointmentTypes.PreSurvey,
            SurveyRequestId = request.SurveyRequestId,
            PropertySiteId = request.PropertySiteId,
            OperationalZoneId = ctx.Coverage.Zone?.Id,
            StartsAt = start,
            EndsAt = end,
            ReservedTechnicianUserId = technicianUserId,
            AssignedTechnicianUserId = technicianUserId,
            AssignedBySaleUserId = assignedBy,
            Status = AppointmentStatuses.Assigned,
            AssignmentSource = AssignmentSources.ManagerDispatch,
            AssignmentReason = reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength],
            AssignedAt = now,
            CustomerNote = request.CustomerNote is { Length: > 1000 } note ? note[..1000] : request.CustomerNote,
            BookedByUserId = request.CustomerUserId,
            BookedAt = now,
            CrossZoneAuthorizationId = authorizationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        scheduling.AddAppointment(appointment);
        if (!await scheduling.SetSurveyRequestScheduleAsync(request.SurveyRequestId, start, token))
            return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "The survey request can no longer be scheduled.");
        AppointmentHistory.Record(scheduling, appointment, AppointmentEventTypes.Booked, actorUserId, now, newTechnicianUserId: technicianUserId, newStartsAt: start,
            reason: $"DISPATCH_REQUEST {request.Id}");
        AppointmentAssignmentService.RecordStaffAssignment(scheduling, appointment, AssignmentSources.ManagerDispatch, reason, assignedBy, technicianUserId,
            null, null, survey.AssignedSaleId, now);
        return ApplyResult.Applied(appointment.Id);
    }

    private Guid Authorize(DispatchRequest request, Guid? installationProjectId, Guid technicianUserId, Guid? targetZoneId, string reason, Guid actorUserId, DateTimeOffset now)
    {
        var authorization = new CrossZoneAuthorization
        {
            Id = Guid.NewGuid(),
            DispatchRequestId = request.Id,
            InstallationProjectId = installationProjectId,
            TechnicianUserId = technicianUserId,
            TargetZoneId = targetZoneId,
            Reason = reason,
            AuthorizedByUserId = actorUserId,
            AuthorizedAt = now,
        };
        dispatch.Add(authorization);
        return authorization.Id;
    }

    private static void Resolve(DispatchRequest request, string status, string resolution, Guid actorUserId, string? reason, DateTimeOffset now)
    {
        request.Status = status;
        request.Resolution = resolution;
        request.ResolutionReason = reason is { Length: > MaxReasonLength } ? reason[..MaxReasonLength] : reason;
        request.ResolvedAt = now;
        request.ResolvedByUserId = actorUserId;
        request.UpdatedAt = now;
        request.Revision++;
    }

    /// <summary>Terminal resolution in one transaction (dispatch lock → proposal): a live proposal is withdrawn first.</summary>
    private async Task<SchedulingResult<bool>> ResolveAsync(Guid dispatchRequestId, string status, string resolution, string eventType, Guid actorUserId, string reason,
        int? expectedRevision, bool notifyCustomer, CancellationToken cancellationToken)
    {
        try
        {
            return await scheduling.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
            {
                var locked = await dispatch.LockAsync(dispatchRequestId, token);
                if (locked is null) return (DispatchNotFound<bool>(), false);
                if (!DispatchStatuses.IsActive(locked.Status))
                    return (SchedulingResult<bool>.Fail(DispatchErrorCodes.InvalidState, $"The dispatch request is already {locked.Status}."), false);
                if (expectedRevision is { } expected && expected != locked.Revision) return (Stale<bool>(locked.Revision), false);
                var now = time.GetUtcNow();
                foreach (var live in (await dispatch.ListProposalsAsync(locked.Id, token)).Where(p => p.Status == ProposalStatuses.Proposed))
                {
                    var tracked = await dispatch.FindProposalForUpdateAsync(live.Id, token);
                    if (tracked is null) continue;
                    tracked.Status = ProposalStatuses.Withdrawn;
                    tracked.RespondedAt = now;
                    tracked.FailureReason = resolution;
                    tracked.Revision++;
                    await RecordAsync(locked, DispatchEventTypes.ProposalWithdrawn, actorUserId, now, $"request {resolution}", tracked.Id, [], token);
                }
                Resolve(locked, status, resolution, actorUserId, reason, now);
                await RecordAsync(locked, eventType, actorUserId, now, reason, null,
                    notifyCustomer ? [locked.CustomerUserId] : await StaffRecipientsAsync(locked, token), token);
                await dispatch.SaveChangesAsync(token);
                return (SchedulingResult<bool>.Ok(true), true);
            }, cancellationToken);
        }
        catch (DispatchConflictException)
        {
            return Stale<bool>(null);
        }
    }

    /// <summary>PROPOSED → DECLINED / WITHDRAWN with the request back to OPEN, under the dispatch lock. A repeat of the same answer is a no-op.</summary>
    private async Task<SchedulingResult<bool>> CloseProposalAsync(Guid dispatchRequestId, Guid proposalId, string proposalStatus, string eventType, Guid actorUserId,
        string? reason, int? expectedRevision, bool notifyProposer, CancellationToken cancellationToken)
    {
        try
        {
            return await scheduling.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
            {
                var locked = await dispatch.LockAsync(dispatchRequestId, token);
                var proposal = await dispatch.FindProposalForUpdateAsync(proposalId, token);
                if (locked is null || proposal is null || proposal.DispatchRequestId != locked.Id) return (ProposalNotFound<bool>(), false);
                if (proposal.Status == proposalStatus) return (SchedulingResult<bool>.Ok(true), false);
                var now = time.GetUtcNow();
                if (proposal.Status == ProposalStatuses.Proposed && proposal.ExpiresAt <= now) return (ProposalExpiredFailure<bool>(), false);
                if (ProposalStateError<bool>(proposal) is { } state) return (state, false);
                if (expectedRevision is { } expected && expected != proposal.Revision) return (Stale<bool>(proposal.Revision), false);

                proposal.Status = proposalStatus;
                proposal.RespondedAt = now;
                proposal.FailureReason = reason;
                proposal.Revision++;
                if (locked.Status == DispatchStatuses.ProposalPending)
                {
                    locked.Status = DispatchStatuses.Open;
                    locked.UpdatedAt = now;
                    locked.Revision++;
                }
                await RecordAsync(locked, eventType, actorUserId, now, reason, proposal.Id,
                    notifyProposer ? [proposal.ProposedByUserId] : [locked.CustomerUserId], token);
                await dispatch.SaveChangesAsync(token);
                return (SchedulingResult<bool>.Ok(true), true);
            }, cancellationToken);
        }
        catch (DispatchConflictException)
        {
            return Stale<bool>(null);
        }
    }

    /// <summary>PROPOSED → FAILED (precise reason) and the request back to OPEN; false when the proposal had already left PROPOSED.</summary>
    private async Task<bool> MarkProposalFailedAsync(Guid dispatchRequestId, Guid proposalId, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            return await scheduling.ExecuteInTransactionAsync<bool>(async token =>
            {
                var locked = await dispatch.LockAsync(dispatchRequestId, token);
                var proposal = await dispatch.FindProposalForUpdateAsync(proposalId, token);
                if (locked is null || proposal is null || proposal.Status != ProposalStatuses.Proposed) return (false, false);
                var now = time.GetUtcNow();
                var detail = $"{code}: {message}";
                proposal.Status = ProposalStatuses.Failed;
                proposal.RespondedAt = now;
                proposal.FailureReason = detail.Length <= MaxReasonLength ? detail : detail[..MaxReasonLength];
                proposal.Revision++;
                if (locked.Status == DispatchStatuses.ProposalPending)
                {
                    locked.Status = DispatchStatuses.Open;
                    locked.UpdatedAt = now;
                    locked.Revision++;
                }
                await RecordAsync(locked, DispatchEventTypes.ProposalFailed, null, now, proposal.FailureReason, proposal.Id,
                    new[] { proposal.ProposedByUserId }.Concat(await StaffRecipientsAsync(locked, token)).ToList(), token);
                await dispatch.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (DispatchConflictException)
        {
            return false;
        }
    }

    /// <summary>True when one of the windows contains a bookable slot an eligible zone technician can hold (then no dispatch is needed).</summary>
    private async Task<bool> AnyBookableSlotAsync(Guid propertySiteId, IReadOnlyList<PreferredWindowInput> windows, SchedulingPolicySnapshot policy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var site = await scheduling.GetSiteCoverageAsync(propertySiteId, cancellationToken);
        if (site.Status != CoverageStatuses.Covered) return false;
        foreach (var window in windows)
        {
            var from = window.StartsAt.ToUniversalTime();
            var to = window.EndsAt.ToUniversalTime();
            for (var date = calendar.LocalDateOf(from); date <= calendar.LocalDateOf(to.AddTicks(-1)); date = date.AddDays(1))
            {
                var technicians = await Eligibility.LoadDaysAsync(date, new TechnicianScope(ZoneIds: site.ZoneIds), cancellationToken);
                foreach (var slot in calendar.Bookable(calendar.CandidateWindows(date, technicians), date, now, policy.Booking)
                             .Where(s => s.StartsAtUtc >= from && s.EndsAtUtc <= to))
                    if (Eligibility.Rank(technicians, new EligibilityRequest(slot.StartsAtUtc, slot.EndsAtUtc, site.ZoneIds)).Count > 0) return true;
            }
        }
        return false;
    }

    private Dictionary<string, string[]>? ValidateWindows(IReadOnlyList<PreferredWindowInput> windows, DateTimeOffset now, SchedulingPolicySnapshot policy)
    {
        var errors = new Dictionary<string, string[]>();
        if (windows.Count > MaxPreferredWindows) errors["preferredWindows"] = [$"At most {MaxPreferredWindows} preferred windows are allowed."];
        var (_, last) = calendar.BookingRange(now, policy.Booking);
        for (var i = 0; i < windows.Count && i < MaxPreferredWindows; i++)
        {
            var w = windows[i];
            if (w.EndsAt <= w.StartsAt) errors[$"preferredWindows[{i}]"] = ["'endsAt' must be after 'startsAt'."];
            else if (w.StartsAt <= now) errors[$"preferredWindows[{i}]"] = ["The window must start in the future."];
            else if (w.EndsAt - w.StartsAt > TimeSpan.FromHours(MaxWindowHours)) errors[$"preferredWindows[{i}]"] = [$"A window may last at most {MaxWindowHours} hours."];
            else if (calendar.LocalDateOf(w.StartsAt) > last) errors[$"preferredWindows[{i}]"] = [$"The window must start on or before {last:yyyy-MM-dd} (booking horizon)."];
        }
        return errors.Count > 0 ? errors : null;
    }

    // ================================================================== notifications & recipients

    /// <summary>One dispatch_event plus a NOT_DELIVERED notification per distinct recipient.</summary>
    private Task RecordAsync(DispatchRequest request, string eventType, Guid? actorUserId, DateTimeOffset now, string? detail, Guid? proposalId,
        IReadOnlyCollection<Guid> notify, CancellationToken token)
    {
        DispatchHistory.Record(dispatch, request.Id, eventType, actorUserId, now, detail, proposalId, notify);
        return Task.CompletedTask;
    }

    /// <summary>The zone's responsible manager when set, otherwise every active Manager (Admins when there is none).</summary>
    private Task<IReadOnlyList<Guid>> StaffRecipientsAsync(DispatchRequest request, CancellationToken token) => DispatchHistory.StaffRecipientsAsync(dispatch, request, token);

    // ================================================================== views

    private async Task<DispatchRequest?> OwnedLatestAsync(Guid customerUserId, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var request = await dispatch.FindLatestForSurveyRequestAsync(surveyRequestId, cancellationToken);
        return request is not null && request.CustomerUserId == customerUserId ? request : null;
    }

    private async Task<(DispatchRequest? Request, DispatchProposal? Proposal)> OwnedProposalAsync(Guid customerUserId, Guid surveyRequestId, Guid proposalId,
        CancellationToken cancellationToken)
    {
        var request = await OwnedLatestAsync(customerUserId, surveyRequestId, cancellationToken);
        if (request is null) return (null, null);
        var proposal = (await dispatch.ListProposalsAsync(request.Id, cancellationToken)).FirstOrDefault(p => p.Id == proposalId);
        return (request, proposal);
    }

    private async Task<CustomerDispatchView> CustomerViewAsync(DispatchRequest r, bool created, CancellationToken cancellationToken)
    {
        var windows = await dispatch.ListWindowsAsync(r.Id, cancellationToken);
        var proposals = await dispatch.ListProposalsAsync(r.Id, cancellationToken);
        var names = await NamesAsync(proposals.Select(p => (Guid?)p.TechnicianUserId), cancellationToken);
        CustomerProposalView? View(DispatchProposal? p) => p is null ? null
            : new CustomerProposalView(p.Id, Local(p.StartsAt), Local(p.EndsAt), names.GetValueOrDefault(p.TechnicianUserId, ""), p.Status, Local(p.ExpiresAt),
                p.RespondedAt is { } at ? Local(at) : null, p.Revision);
        return new CustomerDispatchView(r.Id, r.SurveyRequestId, r.Status, r.Reason, Local(r.CreatedAt), Local(r.ReviewDueAt), r.CustomerNote,
            windows.Select(w => new PreferredWindowView(Local(w.StartsAt), Local(w.EndsAt))).ToList(),
            View(proposals.FirstOrDefault(p => p.Status == ProposalStatuses.Proposed)),
            View(proposals.OrderByDescending(p => p.CreatedAt).FirstOrDefault()),
            r.Resolution, r.ResolutionReason, r.ResolvedAt is { } resolved ? Local(resolved) : null, r.AppointmentId, r.Revision, created);
    }

    private async Task<DispatchStaffView> StaffViewAsync(DispatchQueueRow row, CancellationToken cancellationToken)
    {
        var r = row.Request;
        var now = time.GetUtcNow();
        var proposals = await dispatch.ListProposalsAsync(r.Id, cancellationToken);
        var names = await NamesAsync(proposals.Select(p => (Guid?)p.TechnicianUserId), cancellationToken);
        var site = await scheduling.GetSiteCoverageAsync(r.PropertySiteId, cancellationToken);
        return new DispatchStaffView(Item(row, now, names), r.CustomerNote,
            (await dispatch.ListWindowsAsync(r.Id, cancellationToken)).Select(w => new PreferredWindowView(Local(w.StartsAt), Local(w.EndsAt))).ToList(),
            proposals.OrderByDescending(p => p.CreatedAt).Select(p => Staff(p, names)).ToList(),
            (await dispatch.ListAuthorizationsAsync(r.Id, cancellationToken))
                .Select(a => new CrossZoneAuthorizationView(a.Id, a.TechnicianUserId, a.TargetZoneId, a.Reason, a.AuthorizedByUserId, Local(a.AuthorizedAt))).ToList(),
            (await dispatch.ListEventsAsync(r.Id, cancellationToken))
                .Select(e => new DispatchEventView(e.Id, e.EventType, e.ActorUserId, e.ProposalId, e.Detail, Local(e.CreatedAt))).ToList(),
            r.Resolution, r.ResolutionReason, r.ResolvedAt is { } at ? Local(at) : null, r.ResolvedByUserId, r.AppointmentId, site.Status,
            r.ReleasedAppointmentId);
    }

    private DispatchQueueItemView Item(DispatchQueueRow row, DateTimeOffset now, IReadOnlyDictionary<Guid, string> names)
    {
        var r = row.Request;
        var sla = new DispatchSlaView(r.ReviewSlaMinutes, Local(r.ReviewDueAt), Local(r.EscalationDueAt), r.ReminderIntervalMinutes, r.SettingsVersion,
            IsOverdue(r, now), r.OverdueAt is { } o ? Local(o) : null, r.EscalatedAt is not null, r.EscalatedAt is { } e ? Local(e) : null,
            r.ReminderCount, r.LastReminderAt is { } l ? Local(l) : null);
        return new DispatchQueueItemView(r.Id, r.SurveyRequestId, r.PropertySiteId, r.Status, r.Reason, row.CustomerName, row.PropertyName, row.Province, row.CommuneCode,
            ZoneView.From(row.Zone), Local(r.CreatedAt), sla, row.LiveProposal is { } p ? Staff(p, names) : null, r.Revision);
    }

    /// <summary>Live: waiting for staff (OPEN) past the snapshotted review deadline.</summary>
    public static bool IsOverdue(DispatchRequest request, DateTimeOffset now) => request.Status == DispatchStatuses.Open && request.ReviewDueAt <= now;

    private StaffProposalView Staff(DispatchProposal p, IReadOnlyDictionary<Guid, string> names) => new(
        p.Id, p.TechnicianUserId, names.GetValueOrDefault(p.TechnicianUserId, ""), Local(p.StartsAt), Local(p.EndsAt), p.CrossZone, p.CrossZoneAuthorizationId,
        p.ProposedByUserId, p.Note, Local(p.CreatedAt), p.ResponseWindowMinutes, p.SettingsVersion, Local(p.ExpiresAt), p.Status,
        p.RespondedAt is { } at ? Local(at) : null, p.FailureReason, p.Revision);

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var found = await dispatch.GetUserNamesAsync(wanted, cancellationToken);
        return wanted.ToDictionary(id => id, id => found.GetValueOrDefault(id) ?? "");
    }

    private DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, calendar.TimeZone);

    // ================================================================== results

    private const string ProposalExpiredMessage = "This proposal expired. Our staff can send you another one.";

    private static string StartMessage(SchedulingPolicySnapshot policy)
        => $"The start must be in the future (at least {policy.MinimumBookingLeadMinutes} minutes from now) and within the next {policy.BookingHorizonDays} days.";

    private static SchedulingResult<T>? ProposalStateError<T>(DispatchProposal proposal) => proposal.Status switch
    {
        ProposalStatuses.Proposed => null,
        ProposalStatuses.Expired => ProposalExpiredFailure<T>(),
        _ => SchedulingResult<T>.Fail(DispatchErrorCodes.ProposalNotPending, $"This proposal is {proposal.Status}.", new { proposalStatus = proposal.Status }),
    };

    private static SchedulingResult<T> ProposalExpiredFailure<T>()
        => SchedulingResult<T>.Fail(DispatchErrorCodes.ProposalExpired, ProposalExpiredMessage, new { proposalStatus = ProposalStatuses.Expired });

    private static SchedulingResult<T> SurveyNotFound<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
    private static SchedulingResult<T> DispatchNotFound<T>() => SchedulingResult<T>.Fail(DispatchErrorCodes.DispatchRequestNotFound, "Dispatch request was not found.");
    private static SchedulingResult<T> ProposalNotFound<T>() => SchedulingResult<T>.Fail(DispatchErrorCodes.ProposalNotFound, "Proposal was not found.");

    private static SchedulingResult<T> Stale<T>(int? currentRevision)
        => SchedulingResult<T>.Fail(DispatchErrorCodes.StaleRevision, "The dispatch request changed meanwhile. Reload and try again.",
            currentRevision is null ? null : new { currentRevision });

    private static SchedulingResult<T> Invalid<T>(string field, string message)
        => SchedulingResult<T>.Fail(SchedulingErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, string[]> { [field] = [message] });

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}

/// <summary>Writes dispatch history and the notifications it owes (shared by the service and the worker sweep).</summary>
/// <summary>A new OPEN dispatch request with its SLA snapshot (review deadline, escalation, reminders, settings version) taken now.</summary>
public static class DispatchRequests
{
    public static DispatchRequest Open(Guid surveyRequestId, Guid propertySiteId, Guid customerUserId, Guid? zoneId, string reason, string? customerNote,
        SchedulingPolicySnapshot policy, DateTimeOffset now, Guid? releasedAppointmentId = null)
    {
        var reviewDue = now.AddMinutes(policy.DispatchReviewSlaMinutes);
        return new DispatchRequest
        {
            Id = Guid.NewGuid(),
            SurveyRequestId = surveyRequestId,
            PropertySiteId = propertySiteId,
            CustomerUserId = customerUserId,
            OperationalZoneId = zoneId,
            Reason = reason,
            Status = DispatchStatuses.Open,
            CustomerNote = customerNote,
            ReviewSlaMinutes = policy.DispatchReviewSlaMinutes,
            ReviewDueAt = reviewDue,
            EscalationDueAt = reviewDue.AddMinutes(policy.DispatchEscalationDelayMinutes),
            ReminderIntervalMinutes = policy.DispatchReminderIntervalMinutes,
            SettingsVersion = policy.Version,
            ReleasedAppointmentId = releasedAppointmentId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}

public static class DispatchHistory
{
    public static DispatchEvent Record(IDispatchUnitOfWork dispatch, Guid dispatchRequestId, string eventType, Guid? actorUserId, DateTimeOffset now, string? detail,
        Guid? proposalId, IEnumerable<Guid> notify)
    {
        var entry = new DispatchEvent
        {
            Id = Guid.NewGuid(),
            DispatchRequestId = dispatchRequestId,
            ProposalId = proposalId,
            EventType = eventType,
            ActorUserId = actorUserId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            CreatedAt = now,
        };
        dispatch.Add(entry);
        foreach (var recipient in notify.Distinct())
            dispatch.Add(new DispatchNotification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = recipient,
                DispatchRequestId = dispatchRequestId,
                EventId = entry.Id,
                Type = $"DISPATCH_{eventType}",
                DeliveryStatus = DispatchNotificationStatuses.NotDelivered,
                CreatedAt = now,
            });
        return entry;
    }

    public static async Task<IReadOnlyList<Guid>> StaffRecipientsAsync(IDispatchUnitOfWork dispatch, DispatchRequest request, CancellationToken token)
    {
        if (request.OperationalZoneId is { } zoneId && await dispatch.GetActiveResponsibleManagerAsync(zoneId, token) is { } responsible) return [responsible];
        var managers = await dispatch.ListActiveUserIdsWithRoleAsync(RoleCodes.Manager, token);
        return managers.Count > 0 ? managers : await dispatch.ListActiveUserIdsWithRoleAsync(RoleCodes.Admin, token);
    }
}
