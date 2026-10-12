using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record TechnicianAvailabilityQuery(Guid SaleUserId, Guid SurveyRequestId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public sealed record AssignTechnicianCommand(Guid SaleUserId, Guid SurveyRequestId, Guid TechnicianUserId, string? Reason = null);

public sealed record SalesRescheduleCommand(Guid SaleUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Reason, Guid? TechnicianUserId);

/// <summary>
/// Technicians who pass every appointment, travel and reservation rule for a claimed survey
/// request (by default at the booked appointment time; the appointment itself is ignored so the
/// currently reserved technician is listed too).
/// </summary>
public sealed class TechnicianAvailabilityHandler(
    ISchedulingUnitOfWork unitOfWork,
    TechnicianAvailabilityEvaluator evaluator,
    ITravelTimeEstimator estimator,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time)
{
    public async Task<SchedulingResult<TechnicianAvailabilityView>> HandleAsync(TechnicianAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(query.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.AssignedSaleId != query.SaleUserId)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(PreSurveyErrorCodes.SurveyRequestNotAssigned, "Survey request is not assigned to you.");
        if (context.Location is not { } location)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired,
                "The property site has no valid GPS coordinates, so travel times cannot be verified.");

        var appointment = context.ActiveAppointment;
        var loader = new TechnicianDayLoader(unitOfWork, calendar);
        DateTimeOffset start, end;
        if (query.StartsAt is { } requested)
        {
            var date = calendar.LocalDateOf(requested);
            var grid = calendar.CandidateWindows(date, await loader.LoadAsync(date, null, cancellationToken));
            var window = grid.FirstOrDefault(w => w.StartsAtUtc == requested.ToUniversalTime());
            if (window is null || window.StartsAtUtc <= time.GetUtcNow() || (query.EndsAt is { } e && e.ToUniversalTime() != window.EndsAtUtc))
                return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.InvalidBookingTime,
                    $"startsAt must be a future slot inside a technician's working hours and endsAt must be startsAt + {options.AppointmentDurationMinutes} minutes.");
            (start, end) = (window.StartsAtUtc, window.EndsAtUtc);
        }
        else if (appointment is not null)
        {
            (start, end) = (appointment.StartsAt, appointment.EndsAt);
        }
        else
        {
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.BookingNotFound,
                "The customer has not booked an appointment yet; pass startsAt and endsAt to check a specific time.");
        }

        var matches = appointment is not null && appointment.StartsAt == start;
        var technicians = await loader.LoadAsync(calendar.LocalDateOf(start), null, cancellationToken);
        var candidate = new AppointmentCandidate(start, end, location, appointment?.Id);
        var evaluation = await evaluator.EvaluateAsync(candidate, technicians, int.MaxValue,
            new RouteBudget(estimator, options.MaxRouteRequestsPerOperation), cancellationToken,
            maxEvaluated: Math.Max(1, options.MaxRouteRequestsPerOperation / 2));

        var views = evaluation.Feasible.Select(o =>
        {
            var inboundDeparture = o.Plan.Previous is null ? start - evaluator.SafetyBuffer - o.Inbound.Duration!.Value : o.Plan.Inbound.DepartureAtUtc;
            return new AvailableTechnicianView(
                o.Day.TechnicianUserId,
                o.Day.DisplayName,
                appointment is not null && appointment.ReservedTechnicianUserId == o.Day.TechnicianUserId,
                o.Day.Appointments.Count(a => a.AppointmentId != candidate.IgnoreAppointmentId),
                TimeZoneInfo.ConvertTime(o.Result.EarliestArrivalUtc ?? start, calendar.TimeZone),
                TravelEstimateViews.From(o.Plan.Inbound, o.Inbound, inboundDeparture, options.SafetyBufferMinutes),
                o.Plan.Outbound is { } leg && o.Outbound is { } outbound
                    ? TravelEstimateViews.From(leg, outbound, leg.DepartureAtUtc, options.SafetyBufferMinutes)
                    : null);
        }).OrderByDescending(v => v.IsCurrentReservation).ThenBy(v => v.Inbound.DurationSeconds).ToList();

        return SchedulingResult<TechnicianAvailabilityView>.Ok(new TechnicianAvailabilityView(
            context.SurveyRequestId,
            TimeZoneInfo.ConvertTime(start, calendar.TimeZone),
            TimeZoneInfo.ConvertTime(end, calendar.TimeZone),
            matches,
            views,
            evaluation.HadUnverifiedRoutes ? (views.Count == 0 ? "UNAVAILABLE" : "PARTIAL") : "VERIFIED"));
    }
}

/// <summary>Shared Sales-side eligibility checks for a technician who should receive work.</summary>
internal static class TechnicianEligibility
{
    public static async Task<(TechnicianIdentity? Technician, string? Code, string? Message)> CheckAsync(
        ISchedulingUnitOfWork unitOfWork, Guid technicianUserId, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(technicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return (null, SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");
        if (!technician.IsActiveUser || technician.Profile is not { IsSchedulingActive: true }
            || GeoPoint.From(technician.Profile.BaseLatitude, technician.Profile.BaseLongitude) is null)
            return (null, SchedulingErrorCodes.TechnicianNotAvailable, "The technician is inactive or has no configured base location.");
        return (technician, null, null);
    }
}

/// <summary>
/// Official technician assignment by the Sales owner of a claimed request (POST assign-technician,
/// also exposed as PUT reassign-technician).
/// <list type="bullet">
/// <item>Assigning the reserved technician confirms the reservation (idempotent when already assigned).</item>
/// <item>Assigning another technician — also after the first one acknowledged — moves the capacity in
/// one atomic update after a full calendar, schedule and travel check; the original assignment stays
/// intact if anything fails.</item>
/// <item>A reassignment always requires a new acknowledgment by the new technician; the old
/// acknowledgment is invalidated and recorded in history, and both technicians' route chains are refreshed.</item>
/// </list>
/// The survey request becomes Scheduled with ScheduledAt = appointment start.
/// </summary>
public sealed class AssignTechnicianHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    ScheduleRecalculationService recalculation,
    TechnicianDayLoader days,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time)
{
    public async Task<SchedulingResult<AssignmentView>> HandleAsync(AssignTechnicianCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.AssignedSaleId != command.SaleUserId)
            return SchedulingResult<AssignmentView>.Fail(PreSurveyErrorCodes.SurveyRequestNotAssigned, "Survey request is not assigned to you.");
        if (context.Status is not (SurveyRequestStatus.Assigned or SurveyRequestStatus.Scheduled))
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, "This survey request cannot be scheduled in its current status.");
        if (context.ActiveAppointment is not { } appointment)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.BookingNotFound, "The customer has not booked an appointment yet.");
        if (appointment.Status is not (AppointmentStatuses.Booked or AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged))
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, "This appointment cannot be assigned in its current status.");

        var now = time.GetUtcNow();
        if (appointment.StartsAt <= now)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, "Past or ongoing appointments cannot be assigned or reassigned.");

        var (technician, code, message) = await TechnicianEligibility.CheckAsync(unitOfWork, command.TechnicianUserId, cancellationToken);
        if (technician is null) return SchedulingResult<AssignmentView>.Fail(code!, message!);

        // Already officially assigned to this technician: nothing changes (an acknowledgment is kept).
        if (appointment.AssignedTechnicianUserId == command.TechnicianUserId && appointment.Status is AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged)
            return SchedulingResult<AssignmentView>.Ok(new AssignmentView(appointment.Id, appointment.SurveyRequestId!.Value, command.TechnicianUserId, technician.FullName,
                TimeZoneInfo.ConvertTime(appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(appointment.EndsAt, calendar.TimeZone),
                appointment.Status, "SCHEDULED", TimeZoneInfo.ConvertTime(appointment.StartsAt, calendar.TimeZone), false,
                appointment.Status == AppointmentStatuses.Assigned));

        if (context.Location is not { } location)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired, "The property site has no valid GPS coordinates.");

        var date = calendar.LocalDateOf(appointment.StartsAt);
        var candidate = new AppointmentCandidate(appointment.StartsAt, appointment.EndsAt, location, appointment.Id);
        var reassigned = command.TechnicianUserId != appointment.ReservedTechnicianUserId;

        try
        {
            var result = reassigned
                ? await ReassignAsync(command, appointment, candidate, date, technician, cancellationToken)
                : await ConfirmReservationAsync(command, appointment, candidate, date, technician, cancellationToken);
            if (result.Succeeded && reassigned)
            {
                // The previous technician's day lost a stop: refresh its route chain now (retried later if this fails).
                await recalculation.TryRunAsync(appointment.ReservedTechnicianUserId, date, cancellationToken);
            }
            return result;
        }
        catch (SchedulingConflictException)
        {
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed while assigning. Reload and try again.");
        }
    }

    private async Task<SchedulingResult<AssignmentView>> ConfirmReservationAsync(
        AssignTechnicianCommand command, AppointmentSnapshot snapshot, AppointmentCandidate candidate,
        DateOnly date, TechnicianIdentity technician, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<AssignmentView>>(async token =>
        {
            await unitOfWork.LockTechniciansAsync([command.TechnicianUserId], token);
            var day = (await days.LoadAsync(date, command.TechnicianUserId, token)).SingleOrDefault();
            if (day is null)
                return (SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.TechnicianNotAvailable, "The technician is no longer available for scheduling."), false);

            // The reservation was travel-verified when booked and neighbours were checked against it
            // since; re-check what can change independently: schedule, leave, limits and overlaps.
            var reason = FeasibilityRules.PreCheck(day, candidate, day.MaxAppointmentsPerDayOverride ?? options.MaxAppointmentsPerTechnicianPerDay);
            if (reason is not null)
                return (SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.TechnicianNotAvailable, $"The technician cannot take this appointment ({reason})."), false);

            var appointment = await unitOfWork.FindAppointmentForUpdateAsync(snapshot.Id, token);
            if (appointment is null || appointment.Revision != snapshot.Revision || appointment.ReservedTechnicianUserId != command.TechnicianUserId
                || appointment.Status is not (AppointmentStatuses.Booked or AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged))
                return (SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed while assigning. Reload and try again."), false);

            var now = time.GetUtcNow();
            var previousAssignee = appointment.AssignedTechnicianUserId;
            var wasAcknowledged = appointment.Status == AppointmentStatuses.Acknowledged;
            MarkAssigned(appointment, command, now);
            if (!await unitOfWork.TryMarkSurveyRequestScheduledAsync(command.SurveyRequestId, command.SaleUserId, appointment.StartsAt, token))
                return (SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, "The survey request changed while assigning."), false);

            RecordAssignment(appointment, command, previousAssignee, wasAcknowledged, reassigned: false, now);
            await unitOfWork.SaveChangesAsync(token);
            return (SchedulingResult<AssignmentView>.Ok(View(appointment, technician.FullName, false)), true);
        }, cancellationToken);
        return result;
    }

    private async Task<SchedulingResult<AssignmentView>> ReassignAsync(
        AssignTechnicianCommand command, AppointmentSnapshot snapshot, AppointmentCandidate candidate,
        DateOnly date, TechnicianIdentity technician, CancellationToken cancellationToken)
    {
        TechnicianAppointment? updated = null;
        var spec = new ReservationSpec(candidate, date, command.TechnicianUserId, [snapshot.ReservedTechnicianUserId], TravelEstimateContexts.Assignment);
        var outcome = await reservations.ReserveAsync(spec, async (ctx, token) =>
        {
            var appointment = await unitOfWork.FindAppointmentForUpdateAsync(snapshot.Id, token);
            if (appointment is null || appointment.Revision != snapshot.Revision
                || appointment.Status is not (AppointmentStatuses.Booked or AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged))
                return ApplyResult.Abort(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed while assigning. Reload and try again.");

            var previousReserved = appointment.ReservedTechnicianUserId;
            var previousAssignee = appointment.AssignedTechnicianUserId;
            var wasAcknowledged = appointment.Status == AppointmentStatuses.Acknowledged;

            // One UPDATE moves the capacity: the new technician is reserved and the old one released
            // atomically (the exclusion constraint validates the new technician's calendar). The
            // appointment is never assigned to two technicians.
            appointment.ReservedTechnicianUserId = command.TechnicianUserId;
            MarkAssigned(appointment, command, ctx.NowUtc);
            if (!await unitOfWork.TryMarkSurveyRequestScheduledAsync(command.SurveyRequestId, command.SaleUserId, appointment.StartsAt, token))
                return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "The survey request changed while assigning.");

            RecordAssignment(appointment, command, previousAssignee ?? previousReserved, wasAcknowledged, reassigned: true, ctx.NowUtc);
            await unitOfWork.RequestRecalculationAsync(previousReserved, date, RecalculationReasons.Reassignment, ctx.NowUtc, token);
            updated = appointment;
            return ApplyResult.Applied(appointment.Id);
        }, cancellationToken);

        return outcome.Succeeded
            ? SchedulingResult<AssignmentView>.Ok(View(updated!, technician.FullName, true))
            : SchedulingResult<AssignmentView>.Fail(outcome.ErrorCode!,
                outcome.ErrorCode == SchedulingErrorCodes.TechnicianNotAvailable
                    ? outcome.Message! + " The current assignment is unchanged."
                    : outcome.Message!);
    }

    private void RecordAssignment(TechnicianAppointment appointment, AssignTechnicianCommand command, Guid? previousTechnician, bool wasAcknowledged, bool reassigned, DateTimeOffset now)
    {
        if (wasAcknowledged)
        {
            // The old acknowledgment does not carry over: the new assignment must be acknowledged again.
            AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.AcknowledgementInvalidated, command.SaleUserId, now,
                previousTechnician, command.TechnicianUserId, reason: reassigned ? "REASSIGNED" : "REASSIGNMENT_CONFIRMED", notify: [previousTechnician]);
        }

        AppointmentHistory.Record(unitOfWork, appointment,
            reassigned || (previousTechnician is not null && previousTechnician != command.TechnicianUserId) ? AppointmentEventTypes.Reassigned : AppointmentEventTypes.Assigned,
            command.SaleUserId, now, previousTechnician, command.TechnicianUserId, appointment.StartsAt, appointment.StartsAt,
            string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim(),
            notify: [command.TechnicianUserId, previousTechnician == command.TechnicianUserId ? null : previousTechnician, appointment.BookedByUserId]);
    }

    internal static void MarkAssigned(TechnicianAppointment appointment, AssignTechnicianCommand command, DateTimeOffset now)
    {
        appointment.Status = AppointmentStatuses.Assigned;
        appointment.AssignedTechnicianUserId = command.TechnicianUserId;
        appointment.AssignedBySaleUserId = command.SaleUserId;
        appointment.AssignedAt = now;
        appointment.AcknowledgedAt = null;
        appointment.UpdatedAt = now;
        appointment.Revision++;
    }

    private AssignmentView View(TechnicianAppointment a, string technicianName, bool reassigned) => new(
        a.Id, a.SurveyRequestId!.Value, a.AssignedTechnicianUserId!.Value, technicianName,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
        a.Status, "SCHEDULED", TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), reassigned, true);
}

/// <summary>
/// Sales-managed rescheduling of an owned request (the customer can no longer change it once a
/// technician is officially assigned). The new time is fully re-validated for the assigned (or
/// explicitly given) technician: working schedule, leave, daily limit, previous and next
/// appointments and Vietmap travel. The old appointment is cancelled and replaced in one
/// transaction, so a failure leaves the original appointment untouched. A changed time always
/// requires a new technician acknowledgment.
/// </summary>
public sealed class SalesRescheduleHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    ScheduleRecalculationService recalculation,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<SalesRescheduleView>> HandleAsync(SalesRescheduleCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.AssignedSaleId != command.SaleUserId)
            return SchedulingResult<SalesRescheduleView>.Fail(PreSurveyErrorCodes.SurveyRequestNotAssigned, "Survey request is not assigned to you.");
        if (context.Status is not (SurveyRequestStatus.Assigned or SurveyRequestStatus.Scheduled))
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.InvalidBookingState, "This survey request cannot be rescheduled in its current status.");
        if (context.ActiveAppointment is not { } current)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.BookingNotFound, "There is no active appointment to reschedule.");

        var now = time.GetUtcNow();
        if (current.StartsAt <= now)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.InvalidBookingState, "Past or ongoing appointments cannot be rescheduled.");

        var targetTechnicianId = command.TechnicianUserId ?? current.ReservedTechnicianUserId;
        var (technician, code, message) = await TechnicianEligibility.CheckAsync(unitOfWork, targetTechnicianId, cancellationToken);
        if (technician is null)
            return SchedulingResult<SalesRescheduleView>.Fail(code!, command.TechnicianUserId is null
                ? message! + " Pass technicianUserId to reschedule and reassign together."
                : message!);

        if (BookSurveyAppointmentHandler.SameStart(current, command.StartsAt) && targetTechnicianId == current.ReservedTechnicianUserId)
            return SchedulingResult<SalesRescheduleView>.Ok(View(current, null, technician.FullName, context.Status, false, false));

        if (context.Location is not { } location)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired, "The property site has no valid GPS coordinates.");

        var (window, reason) = await SlotGrid.ResolveAsync(unitOfWork, calendar, command.StartsAt, now, cancellationToken);
        if (window is null)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.InvalidBookingTime, reason!);

        var oldDate = calendar.LocalDateOf(current.StartsAt);
        var reassigned = targetTechnicianId != current.ReservedTechnicianUserId;
        var reasonText = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();
        var spec = new ReservationSpec(new AppointmentCandidate(window.StartsAtUtc, window.EndsAtUtc, location, current.Id),
            window.LocalDate, targetTechnicianId, [current.ReservedTechnicianUserId], TravelEstimateContexts.Reschedule);

        TechnicianAppointment? replacement = null;
        var surveyStatus = context.Status;
        ReservationOutcome outcome;
        try
        {
            outcome = await reservations.ReserveAsync(spec, async (ctx, token) =>
            {
                var old = await unitOfWork.FindAppointmentForUpdateAsync(current.Id, token);
                if (old is null || old.Revision != current.Revision
                    || old.Status is not (AppointmentStatuses.Booked or AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged))
                    return ApplyResult.Abort(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed meanwhile. Reload and try again.");

                var wasAcknowledged = old.Status == AppointmentStatuses.Acknowledged;
                var keepBooked = old.Status == AppointmentStatuses.Booked && !reassigned;
                RescheduleSurveyAppointmentHandler.CancelForReplacement(old, ctx.NowUtc);
                old.CancellationReason = "RESCHEDULED_BY_SALES";
                await unitOfWork.SaveChangesAsync(token);

                var next = BookSurveyAppointmentHandler.NewAppointment(context, window, targetTechnicianId, old.BookedByUserId, old.CustomerNote, ctx.NowUtc);
                next.BookedAt = old.BookedAt;
                if (!keepBooked)
                {
                    next.Status = AppointmentStatuses.Assigned;
                    next.AssignedTechnicianUserId = targetTechnicianId;
                    next.AssignedBySaleUserId = command.SaleUserId;
                    next.AssignedAt = ctx.NowUtc;
                }
                unitOfWork.AddAppointment(next);
                old.ReplacedByAppointmentId = next.Id;

                if (!keepBooked)
                {
                    if (!await unitOfWork.TryMarkSurveyRequestScheduledAsync(command.SurveyRequestId, command.SaleUserId, next.StartsAt, token))
                        return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "The survey request changed meanwhile.");
                    surveyStatus = SurveyRequestStatus.Scheduled;
                }

                var previousAssignee = old.AssignedTechnicianUserId ?? old.ReservedTechnicianUserId;
                if (wasAcknowledged)
                    AppointmentHistory.Record(unitOfWork, next, AppointmentEventTypes.AcknowledgementInvalidated, command.SaleUserId, ctx.NowUtc,
                        previousAssignee, targetTechnicianId, old.StartsAt, next.StartsAt, "RESCHEDULED", notify: [previousAssignee]);
                AppointmentHistory.Record(unitOfWork, next, AppointmentEventTypes.Rescheduled, command.SaleUserId, ctx.NowUtc,
                    previousAssignee, targetTechnicianId, old.StartsAt, next.StartsAt, reasonText ?? "SALES_RESCHEDULE",
                    notify: [targetTechnicianId, previousAssignee, old.BookedByUserId]);
                if (reassigned)
                    AppointmentHistory.Record(unitOfWork, next, AppointmentEventTypes.Reassigned, command.SaleUserId, ctx.NowUtc,
                        previousAssignee, targetTechnicianId, old.StartsAt, next.StartsAt, reasonText);

                await unitOfWork.RequestRecalculationAsync(old.ReservedTechnicianUserId, oldDate, RecalculationReasons.SalesReschedule, ctx.NowUtc, token);
                replacement = next;
                return ApplyResult.Applied(next.Id);
            }, cancellationToken);
        }
        catch (SchedulingConflictException)
        {
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed meanwhile. Reload and try again.");
        }

        if (!outcome.Succeeded)
        {
            var hint = outcome.ErrorCode == SchedulingErrorCodes.TechnicianNotAvailable && command.TechnicianUserId is null
                ? " The assigned technician cannot serve the new time; the original appointment is unchanged. Choose another time or pass technicianUserId to reschedule and reassign together."
                : " The original appointment is unchanged.";
            return SchedulingResult<SalesRescheduleView>.Fail(outcome.ErrorCode!, outcome.Message! + hint);
        }

        await recalculation.TryRunAsync(current.ReservedTechnicianUserId, oldDate, cancellationToken);

        var saved = await unitOfWork.GetAppointmentAsync(replacement!.Id, cancellationToken);
        return SchedulingResult<SalesRescheduleView>.Ok(View(saved!, current.Id, technician.FullName, surveyStatus, reassigned,
            saved!.Status == AppointmentStatuses.Assigned));
    }

    private SalesRescheduleView View(AppointmentSnapshot a, Guid? previousId, string technicianName, SurveyRequestStatus surveyStatus, bool reassigned, bool acknowledgementRequired) => new(
        a.Id, previousId, a.SurveyRequestId!.Value, a.ReservedTechnicianUserId, technicianName,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
        a.Status, surveyStatus.ToString().ToUpperInvariant(),
        surveyStatus == SurveyRequestStatus.Scheduled ? TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone) : null,
        reassigned, acknowledgementRequired);
}
