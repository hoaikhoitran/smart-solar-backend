using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record GetSurveySlotsQuery(Guid CustomerUserId, Guid PropertySiteId, DateOnly Date);

public sealed record BookSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Note);

public sealed record RescheduleSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Note);

public sealed record CancelSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, string? Reason);

/// <summary>
/// Dynamic slots for a customer's property site on one local date. Candidate start times are the
/// union of every eligible technician's own working schedule; a slot is listed only when at least
/// one technician passes every rule with verified motorcycle travel times.
/// </summary>
public sealed class GetSurveySlotsHandler(
    ISchedulingUnitOfWork unitOfWork,
    TechnicianAvailabilityEvaluator evaluator,
    ITravelTimeEstimator estimator,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time)
{
    public async Task<SchedulingResult<SurveySlotsView>> HandleAsync(GetSurveySlotsQuery query, CancellationToken cancellationToken)
    {
        var site = await unitOfWork.GetPropertySiteAsync(query.PropertySiteId, cancellationToken);
        if (site is null || site.CustomerUserId != query.CustomerUserId)
            return SchedulingResult<SurveySlotsView>.Fail(SchedulingErrorCodes.PropertySiteNotFound, "Property site was not found.");
        if (site.Location is not { } location)
            return SchedulingResult<SurveySlotsView>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired,
                "The property site has no valid GPS coordinates, so travel times cannot be verified.");

        var now = time.GetUtcNow();
        var (first, last) = calendar.BookingRange(now);
        if (query.Date < first || query.Date > last)
            return SchedulingResult<SurveySlotsView>.Fail(SchedulingErrorCodes.InvalidBookingTime,
                $"Choose a date between {first:yyyy-MM-dd} and {last:yyyy-MM-dd}.");

        var technicians = await new TechnicianDayLoader(unitOfWork, calendar).LoadAsync(query.Date, null, cancellationToken);
        var windows = calendar.Bookable(calendar.CandidateWindows(query.Date, technicians), query.Date, now);

        var budget = new RouteBudget(estimator, options.MaxRouteRequestsPerOperation);
        var slots = new List<SurveySlotView>();
        var unverified = 0;
        foreach (var window in windows)
        {
            var evaluation = await evaluator.EvaluateAsync(
                new AppointmentCandidate(window.StartsAtUtc, window.EndsAtUtc, location), technicians,
                Math.Max(1, options.MaxReportedSlotCapacity), budget, cancellationToken);

            if (evaluation.Feasible.Count > 0)
            {
                slots.Add(new SurveySlotView(
                    TimeZoneInfo.ConvertTime(window.StartsAtUtc, calendar.TimeZone),
                    TimeZoneInfo.ConvertTime(window.EndsAtUtc, calendar.TimeZone),
                    window.StartsAtUtc, window.EndsAtUtc,
                    CanBook: true,
                    evaluation.Exhaustive ? CapacityStatuses.Exact : CapacityStatuses.AtLeast,
                    evaluation.Feasible.Count,
                    "AVAILABLE"));
            }
            else if (evaluation.HadUnverifiedRoutes)
            {
                unverified++;
            }
        }

        var routing = unverified == 0 ? "VERIFIED" : slots.Count == 0 ? "UNAVAILABLE" : "PARTIAL";
        var note = technicians.Count == 0
            ? "No technician with a configured base location is available on this date."
            : windows.Count == 0 && slots.Count == 0 && unverified == 0
                ? "No technician works on this date, or every start time has passed."
                : unverified > 0 ? $"{unverified} time(s) could not be verified because travel times were unavailable; they are not offered." : null;

        return SchedulingResult<SurveySlotsView>.Ok(new SurveySlotsView(
            query.Date, calendar.TimeZone.Id, options.AppointmentDurationMinutes, slots, routing, unverified, note));
    }
}

/// <summary>Resolves a requested start against the union of the technicians' slot grids on that date.</summary>
internal static class SlotGrid
{
    public static async Task<(AppointmentWindow? Window, string? Reason)> ResolveAsync(
        ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, DateTimeOffset requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var date = calendar.LocalDateOf(requested);
        var technicians = await new TechnicianDayLoader(unitOfWork, calendar).LoadAsync(date, null, cancellationToken);
        var windows = calendar.CandidateWindows(date, technicians);
        if (windows.Count == 0) windows = calendar.CandidateWindows(date);   // explain grid errors even with nobody working
        return calendar.TryResolveBookable(requested, now, windows, out var window, out var reason) ? (window, null) : (null, reason);
    }
}

/// <summary>
/// Books a survey appointment immediately. The internally reserved technician is not exposed;
/// the survey request stays Pending for the Sales queue. A retry with the same start returns the
/// existing booking (idempotent).
/// </summary>
public sealed class BookSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<BookingConfirmation>> HandleAsync(BookSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");

        if (context.ActiveAppointment is { } existing)
            return SameStart(existing, command.StartsAt)
                ? SchedulingResult<BookingConfirmation>.Ok(Confirm(existing, created: false))
                : SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.BookingAlreadyExists,
                    "This survey request already has an active appointment. Reschedule or cancel it instead.");

        if (context.Status is not (SurveyRequestStatus.Pending or SurveyRequestStatus.Assigned))
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingState, "This survey request can no longer be booked.");

        if (context.Location is not { } location)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired,
                "The property site has no valid GPS coordinates, so travel times cannot be verified.");

        var (window, reason) = await SlotGrid.ResolveAsync(unitOfWork, calendar, command.StartsAt, time.GetUtcNow(), cancellationToken);
        if (window is null)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingTime, reason!);

        var spec = new ReservationSpec(new AppointmentCandidate(window.StartsAtUtc, window.EndsAtUtc, location),
            window.LocalDate, null, [], TravelEstimateContexts.Booking);

        ReservationOutcome outcome;
        try
        {
            outcome = await reservations.ReserveAsync(spec, async (ctx, token) =>
            {
                var current = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, token);
                if (current is null || current.ActiveAppointment is not null)
                    return ApplyResult.Abort(SchedulingErrorCodes.BookingAlreadyExists, "This survey request already has an active appointment.");
                if (current.Status is not (SurveyRequestStatus.Pending or SurveyRequestStatus.Assigned))
                    return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "This survey request can no longer be booked.");

                var appointment = NewAppointment(context, window, ctx.Option.Day.TechnicianUserId, command.CustomerUserId, command.Note, ctx.NowUtc);
                unitOfWork.AddAppointment(appointment);
                AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Booked, command.CustomerUserId, ctx.NowUtc,
                    newTechnicianUserId: appointment.ReservedTechnicianUserId, newStartsAt: appointment.StartsAt);
                return ApplyResult.Applied(appointment.Id);
            }, cancellationToken);
        }
        catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.ActiveAppointmentExists)
        {
            // The partial unique index stopped a second active appointment.
            return await ExistingOrConflictAsync(command, cancellationToken);
        }

        // A concurrent identical request (e.g. a network retry) can commit first; the locked re-read
        // then sees its appointment. Same start = the same booking, answered idempotently.
        if (!outcome.Succeeded && outcome.ErrorCode == SchedulingErrorCodes.BookingAlreadyExists)
            return await ExistingOrConflictAsync(command, cancellationToken);

        if (!outcome.Succeeded)
            return SchedulingResult<BookingConfirmation>.Fail(outcome.ErrorCode!, outcome.Message!);

        var booked = await unitOfWork.GetAppointmentAsync(outcome.AppointmentId!.Value, cancellationToken);
        return SchedulingResult<BookingConfirmation>.Ok(Confirm(booked!, created: true));
    }

    private async Task<SchedulingResult<BookingConfirmation>> ExistingOrConflictAsync(BookSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var winner = (await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken))?.ActiveAppointment;
        return winner is not null && SameStart(winner, command.StartsAt)
            ? SchedulingResult<BookingConfirmation>.Ok(Confirm(winner, created: false))
            : SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.BookingAlreadyExists, "This survey request already has an active appointment.");
    }

    internal static TechnicianAppointment NewAppointment(SurveyRequestSchedulingContext context, AppointmentWindow window, Guid technicianId, Guid bookedByUserId, string? note, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            AppointmentType = AppointmentTypes.PreSurvey,
            SurveyRequestId = context.SurveyRequestId,
            PropertySiteId = context.PropertySiteId,
            StartsAt = window.StartsAtUtc,
            EndsAt = window.EndsAtUtc,
            ReservedTechnicianUserId = technicianId,
            Status = AppointmentStatuses.Booked,
            CustomerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            BookedByUserId = bookedByUserId,
            BookedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 0,
        };

    internal static bool SameStart(AppointmentSnapshot appointment, DateTimeOffset requested) => appointment.StartsAt == requested.ToUniversalTime();

    internal BookingConfirmation Confirm(AppointmentSnapshot a, bool created) => new(
        a.Id, a.SurveyRequestId!.Value,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
        a.Status, created,
        created ? "Your survey appointment is booked. Our team will assign a technician." : "This appointment was already booked.");
}

/// <summary>
/// Moves a BOOKED (not yet assigned) future appointment to a new start time. The old appointment
/// is cancelled and linked to its replacement in the same transaction, so history is preserved
/// and a failure leaves the original booking unchanged. The vacated position's route chain is
/// queued for recalculation and recalculated right after the commit.
/// </summary>
public sealed class RescheduleSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    ScheduleRecalculationService recalculation,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<BookingConfirmation>> HandleAsync(RescheduleSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.ActiveAppointment is not { } current)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.BookingNotFound, "There is no active appointment to reschedule.");

        var now = time.GetUtcNow();
        var guard = CustomerChangeGuard.Check(current, now);
        if (guard is not null) return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingState, guard);

        var booking = new BookSurveyAppointmentHandler(unitOfWork, reservations, calendar, time);
        if (BookSurveyAppointmentHandler.SameStart(current, command.StartsAt))
            return SchedulingResult<BookingConfirmation>.Ok(booking.Confirm(current, created: false));

        if (context.Location is not { } location)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.PropertySiteLocationRequired, "The property site has no valid GPS coordinates.");
        var (window, reason) = await SlotGrid.ResolveAsync(unitOfWork, calendar, command.StartsAt, now, cancellationToken);
        if (window is null)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingTime, reason!);

        var spec = new ReservationSpec(new AppointmentCandidate(window.StartsAtUtc, window.EndsAtUtc, location, current.Id),
            window.LocalDate, null, [current.ReservedTechnicianUserId], TravelEstimateContexts.Reschedule);
        var oldDate = calendar.LocalDateOf(current.StartsAt);

        var outcome = await reservations.ReserveAsync(spec, async (ctx, token) =>
        {
            var old = await unitOfWork.FindAppointmentForUpdateAsync(current.Id, token);
            if (old is null || old.Status != AppointmentStatuses.Booked || old.Revision != current.Revision)
                return ApplyResult.Abort(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed (for example it was just assigned). Reload and try again.");

            CancelForReplacement(old, ctx.NowUtc);
            await unitOfWork.SaveChangesAsync(token);

            var replacement = BookSurveyAppointmentHandler.NewAppointment(context, window, ctx.Option.Day.TechnicianUserId, command.CustomerUserId,
                command.Note ?? current.CustomerNote, ctx.NowUtc);
            unitOfWork.AddAppointment(replacement);
            old.ReplacedByAppointmentId = replacement.Id;
            AppointmentHistory.Record(unitOfWork, replacement, AppointmentEventTypes.Rescheduled, command.CustomerUserId, ctx.NowUtc,
                old.ReservedTechnicianUserId, replacement.ReservedTechnicianUserId, old.StartsAt, replacement.StartsAt, "CUSTOMER_RESCHEDULE");
            await unitOfWork.RequestRecalculationAsync(old.ReservedTechnicianUserId, oldDate, RecalculationReasons.CustomerReschedule, ctx.NowUtc, token);
            return ApplyResult.Applied(replacement.Id);
        }, cancellationToken);

        if (!outcome.Succeeded)
            return SchedulingResult<BookingConfirmation>.Fail(outcome.ErrorCode!, outcome.Message!);

        await recalculation.TryRunAsync(current.ReservedTechnicianUserId, oldDate, cancellationToken);

        var booked = await unitOfWork.GetAppointmentAsync(outcome.AppointmentId!.Value, cancellationToken);
        return SchedulingResult<BookingConfirmation>.Ok(booking.Confirm(booked!, created: true) with { Message = "Your survey appointment was rescheduled." });
    }

    internal static void CancelForReplacement(TechnicianAppointment old, DateTimeOffset now)
    {
        old.Status = AppointmentStatuses.Cancelled;
        old.CancelledAt = now;
        old.CancellationReason = "RESCHEDULED";
        old.UpdatedAt = now;
        old.Revision++;
    }
}

/// <summary>
/// Cancels a BOOKED future appointment and releases the technician capacity. History is kept. The
/// cancellation commits regardless of the routing provider; the technician-day is queued for route
/// recalculation in the same transaction and recalculated right after the commit (a failure there
/// leaves an explicit FAILED request that the background worker retries).
/// </summary>
public sealed class CancelSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    ScheduleRecalculationService recalculation,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<CustomerAppointmentView>> HandleAsync(CancelSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.ActiveAppointment is not { } current)
            return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.BookingNotFound, "There is no active appointment to cancel.");

        var guard = CustomerChangeGuard.Check(current, time.GetUtcNow());
        if (guard is not null) return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, guard);

        var date = calendar.LocalDateOf(current.StartsAt);
        try
        {
            var cancelled = await unitOfWork.ExecuteInTransactionAsync<TechnicianAppointment?>(async token =>
            {
                await unitOfWork.LockTechniciansAsync([current.ReservedTechnicianUserId], token);
                var appointment = await unitOfWork.FindAppointmentForUpdateAsync(current.Id, token);
                if (appointment is null || appointment.Status != AppointmentStatuses.Booked || appointment.Revision != current.Revision)
                    return (null, false);

                var now = time.GetUtcNow();
                appointment.Status = AppointmentStatuses.Cancelled;
                appointment.CancelledAt = now;
                appointment.CancellationReason = string.IsNullOrWhiteSpace(command.Reason) ? "CUSTOMER_CANCELLED" : Truncate(command.Reason.Trim(), 200);
                appointment.UpdatedAt = now;
                appointment.Revision++;
                AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Cancelled, command.CustomerUserId, now,
                    previousTechnicianUserId: appointment.ReservedTechnicianUserId, previousStartsAt: appointment.StartsAt, reason: appointment.CancellationReason);
                await unitOfWork.RequestRecalculationAsync(appointment.ReservedTechnicianUserId, date, RecalculationReasons.Cancellation, now, token);
                await unitOfWork.SupersedeEstimatesAsync([appointment.Id], now, token);
                await unitOfWork.SaveChangesAsync(token);
                return (appointment, true);
            }, cancellationToken);

            if (cancelled is null)
                return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed. Reload and try again.");

            await recalculation.TryRunAsync(cancelled.ReservedTechnicianUserId, date, cancellationToken);

            return SchedulingResult<CustomerAppointmentView>.Ok(CustomerViews.Appointment(cancelled.Id, cancelled.StartsAt, cancelled.EndsAt, cancelled.Status,
                null, cancelled.CustomerNote, cancelled.BookedAt, calendar, time.GetUtcNow()));
        }
        catch (SchedulingConflictException)
        {
            return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed. Reload and try again.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>V1 rule: customers change only future appointments that Sales has not assigned yet.</summary>
internal static class CustomerChangeGuard
{
    public static string? Check(AppointmentSnapshot appointment, DateTimeOffset now)
    {
        if (appointment.Status != AppointmentStatuses.Booked)
            return "A technician has already been assigned. Please contact our sales team to change this appointment.";
        if (appointment.StartsAt <= now)
            return "Past or ongoing appointments cannot be changed.";
        return null;
    }
}

internal static class CustomerViews
{
    public static CustomerAppointmentView Appointment(Guid id, DateTimeOffset startsAt, DateTimeOffset endsAt, string status, string? technicianName,
        string? note, DateTimeOffset bookedAt, WorkingCalendar calendar, DateTimeOffset now)
        => new(id,
            TimeZoneInfo.ConvertTime(startsAt, calendar.TimeZone),
            TimeZoneInfo.ConvertTime(endsAt, calendar.TimeZone),
            status,
            status is AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged,
            status is AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged ? technicianName : null,
            note,
            bookedAt,
            status == AppointmentStatuses.Booked && startsAt > now);

    public static string Status(SurveyRequestStatus status) => status.ToString().ToUpperInvariant();
}

/// <summary>The customer's own survey requests with appointment summaries.</summary>
public sealed class CustomerSurveyRequestQueries(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, TimeProvider time)
{
    public async Task<IReadOnlyList<CustomerSurveyRequestView>> ListAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return (await unitOfWork.ListCustomerSurveyRequestsAsync(customerUserId, cancellationToken)).Select(r => Map(r, now)).ToList();
    }

    public async Task<SchedulingResult<CustomerSurveyRequestDetailView>> GetAsync(Guid customerUserId, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var row = (await unitOfWork.ListCustomerSurveyRequestsAsync(customerUserId, cancellationToken)).FirstOrDefault(r => r.SurveyRequestId == surveyRequestId);
        if (row is null)
            return SchedulingResult<CustomerSurveyRequestDetailView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");

        var history = (await unitOfWork.ListAppointmentHistoryAsync(surveyRequestId, cancellationToken))
            .Select(a => new CustomerAppointmentHistoryItem(a.Id,
                TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone), a.Status, a.BookedAt))
            .ToList();
        return SchedulingResult<CustomerSurveyRequestDetailView>.Ok(new CustomerSurveyRequestDetailView(Map(row, time.GetUtcNow()), history));
    }

    private CustomerSurveyRequestView Map(CustomerSurveyRequestRow r, DateTimeOffset now) => new(
        r.SurveyRequestId, r.PreSurveyId, r.PropertySiteId, r.PropertyName, CustomerViews.Status(r.Status), r.SubmittedAt,
        r.ScheduledAt is { } s ? TimeZoneInfo.ConvertTime(s, calendar.TimeZone) : null,
        r.ActiveAppointment is { } a
            ? CustomerViews.Appointment(a.Id, a.StartsAt, a.EndsAt, a.Status, r.AssignedTechnicianName, a.CustomerNote, a.BookedAt, calendar, now)
            : null);
}
