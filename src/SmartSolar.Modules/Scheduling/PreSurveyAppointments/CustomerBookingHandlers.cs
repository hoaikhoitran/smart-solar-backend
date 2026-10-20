using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record GetSurveySlotsQuery(Guid CustomerUserId, Guid PropertySiteId, DateOnly Date);

public sealed record BookSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Note);

public sealed record RescheduleSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Note);

public sealed record CancelSurveyAppointmentCommand(Guid CustomerUserId, Guid SurveyRequestId, string? Reason);

/// <summary>
/// Slots for a customer's property site on one local date. Always 200 for a known site and a date inside the booking horizon:
/// <see cref="SurveySlotsView.CoverageStatus"/> says why there are no slots (SITE_LOCATION_REQUIRED, NO_ZONE_COVERAGE,
/// ZONE_NOT_SERVICEABLE, NO_ZONE_TECHNICIAN, NO_AVAILABLE_TECHNICIAN, or SCHEDULE_CONFLICT = some member works in the horizon but
/// not on this date) and <see cref="SurveySlotsView.DispatchAvailable"/> whether the customer may ask staff for a dispatch instead.
/// Candidate start times are the union of the zone technicians' working schedules; a slot is listed only when at least one
/// technician could hold its capacity. Booking limits come from the current settings snapshot.
/// </summary>
public sealed class GetSurveySlotsHandler(
    ISchedulingUnitOfWork unitOfWork,
    TechnicianEligibility eligibility,
    ZoneCoverageService zoneCoverage,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time)
{
    public async Task<SchedulingResult<SurveySlotsView>> HandleAsync(GetSurveySlotsQuery query, CancellationToken cancellationToken)
    {
        var site = await unitOfWork.GetPropertySiteAsync(query.PropertySiteId, cancellationToken);
        if (site is null || site.CustomerUserId != query.CustomerUserId)
            return SchedulingResult<SurveySlotsView>.Fail(SchedulingErrorCodes.PropertySiteNotFound, "Property site was not found.");

        var policy = await eligibility.PolicyAsync(cancellationToken);
        var now = time.GetUtcNow();
        var (first, last) = calendar.BookingRange(now, policy.Booking);
        if (query.Date < first || query.Date > last)
            return SchedulingResult<SurveySlotsView>.Fail(SchedulingErrorCodes.InvalidBookingTime,
                $"Choose a date between {first:yyyy-MM-dd} and {last:yyyy-MM-dd}.");

        var coverage = await unitOfWork.GetSiteCoverageAsync(site.PropertySiteId, cancellationToken);
        if (coverage.Status != CoverageStatuses.Covered)
            return SchedulingResult<SurveySlotsView>.Ok(View(query, coverage.Zone, [], coverage.Status, CoverageMessages.Describe(coverage.Status)));

        var technicians = await eligibility.LoadDaysAsync(query.Date, new TechnicianScope(ZoneIds: coverage.ZoneIds), cancellationToken);
        var windows = calendar.Bookable(calendar.CandidateWindows(query.Date, technicians), query.Date, now, policy.Booking);
        var cap = Math.Max(1, options.MaxReportedSlotCapacity);

        var slots = new List<SurveySlotView>();
        foreach (var window in windows)
        {
            var eligible = eligibility.Rank(technicians, new EligibilityRequest(window.StartsAtUtc, window.EndsAtUtc, coverage.ZoneIds)).Count;
            if (eligible == 0) continue;
            slots.Add(new SurveySlotView(
                TimeZoneInfo.ConvertTime(window.StartsAtUtc, calendar.TimeZone),
                TimeZoneInfo.ConvertTime(window.EndsAtUtc, calendar.TimeZone),
                window.StartsAtUtc, window.EndsAtUtc,
                CanBook: true,
                eligible > cap ? CapacityStatuses.AtLeast : CapacityStatuses.Exact,
                Math.Min(eligible, cap),
                "AVAILABLE"));
        }
        if (slots.Count > 0) return SchedulingResult<SurveySlotsView>.Ok(View(query, coverage.Zone, slots, CoverageStatuses.Covered, null));

        // No slot on this date: why? The zone may have nobody, nobody free in the whole horizon, or just nobody free on this date.
        var horizon = await zoneCoverage.EvaluateAsync(site.PropertySiteId, cancellationToken: cancellationToken);
        var status = horizon.Status == CoverageStatuses.Covered ? CoverageStatuses.ScheduleConflict : horizon.Status;
        var note = status == CoverageStatuses.ScheduleConflict
            ? "No technician of this zone is free on this date, or every start time has passed. Try another date or request a dispatch."
            : CoverageMessages.Describe(status);
        return SchedulingResult<SurveySlotsView>.Ok(View(query, coverage.Zone, slots, status, note));
    }

    private SurveySlotsView View(GetSurveySlotsQuery query, ZoneRef? zone, IReadOnlyList<SurveySlotView> slots, string status, string? note)
        => new(query.Date, calendar.TimeZone.Id, options.AppointmentDurationMinutes, ZoneView.From(zone), slots, note, status, CoverageStatuses.IsDispatchable(status));
}

/// <summary>Resolves a requested start against the union of the zone technicians' slot grids on that date.</summary>
internal static class SlotGrid
{
    public static async Task<(AppointmentWindow? Window, string? Code, string? Reason)> ResolveAsync(
        ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, WorkingCalendar calendar, Guid propertySiteId,
        DateTimeOffset requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await eligibility.PolicyAsync(cancellationToken);
        var coverage = await unitOfWork.GetSiteCoverageAsync(propertySiteId, cancellationToken);
        if (AppointmentReservationService.CoverageError(coverage) is { } error) return (null, error.ErrorCode, error.Message);

        var date = calendar.LocalDateOf(requested);
        var technicians = await eligibility.LoadDaysAsync(date, new TechnicianScope(ZoneIds: coverage.ZoneIds), cancellationToken);
        var windows = calendar.CandidateWindows(date, technicians);
        if (windows.Count == 0) windows = calendar.CandidateWindows(date);   // explain grid errors even with nobody working
        return calendar.TryResolveBookable(requested, now, policy.Booking, windows, out var window, out var reason)
            ? (window, null, null)
            : (null, SchedulingErrorCodes.InvalidBookingTime, reason);
    }
}

/// <summary>
/// Precise failure of a booking or reschedule without capacity: the coverage code (SITE_LOCATION_REQUIRED, NO_ZONE_COVERAGE,
/// ZONE_NOT_SERVICEABLE, NO_ZONE_TECHNICIAN, NO_AVAILABLE_TECHNICIAN, SCHEDULE_CONFLICT) with details
/// <c>{ coverageStatus, dispatchAvailable }</c>. Nothing is ever confirmed without capacity.
/// </summary>
public static class CapacityFailures
{
    public sealed record CoverageDetails(string CoverageStatus, bool DispatchAvailable);

    public static bool IsCoverageCode(string? code) => code is not null && code != CoverageStatuses.Covered && CoverageStatuses.All.Contains(code);

    public static CoverageDetails Details(string code) => new(code, CoverageStatuses.IsDispatchable(code));

    /// <summary>A failure as is, with coverage details added for coverage codes.</summary>
    public static SchedulingResult<T> Fail<T>(string code, string message)
        => IsCoverageCode(code) ? SchedulingResult<T>.Fail(code, message, Details(code)) : SchedulingResult<T>.Fail(code, message);

    /// <summary>
    /// The reservation found no technician for the window (SURVEY_SLOT_UNAVAILABLE): evaluate the coverage of that window and report
    /// NO_ZONE_TECHNICIAN, NO_AVAILABLE_TECHNICIAN or SCHEDULE_CONFLICT (a COVERED re-read means the capacity was just taken: SCHEDULE_CONFLICT).
    /// </summary>
    public static async Task<SchedulingResult<T>> NoCapacityAsync<T>(ZoneCoverageService coverage, Guid propertySiteId, AppointmentWindow window,
        IReadOnlySet<Guid>? ignore, string suffix, CancellationToken cancellationToken)
    {
        var evaluation = await coverage.EvaluateAsync(propertySiteId, window.StartsAtUtc, window.EndsAtUtc, ignore, cancellationToken);
        var code = evaluation.Status == CoverageStatuses.Covered ? CoverageStatuses.ScheduleConflict : evaluation.Status;
        return SchedulingResult<T>.Fail(code, CoverageMessages.Describe(code) + suffix, Details(code));
    }
}

/// <summary>Shared construction and customer-facing mapping for appointments.</summary>
internal static class Appointments
{
    /// <summary>
    /// A new PENDING_ACCEPTANCE appointment. Its acceptance deadline is derived from <paramref name="policy"/> (the settings current at
    /// this booking) and stored with the window and settings version it used: later settings changes never move it.
    /// </summary>
    public static TechnicianAppointment New(SurveyRequestSchedulingContext context, AppointmentWindow window, ReservationContext reservation,
        Guid bookedByUserId, string? note, SchedulingPolicySnapshot policy, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            AppointmentType = AppointmentTypes.PreSurvey,
            SurveyRequestId = context.SurveyRequestId,
            PropertySiteId = context.PropertySiteId,
            OperationalZoneId = reservation.Coverage.Zone?.Id,
            StartsAt = window.StartsAtUtc,
            EndsAt = window.EndsAtUtc,
            ReservedTechnicianUserId = reservation.Technician.TechnicianUserId,
            Status = AppointmentStatuses.PendingAcceptance,
            CustomerNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            BookedByUserId = bookedByUserId,
            BookedAt = now,
            AcceptanceDeadlineAt = Deadline(now, window.StartsAtUtc, policy),
            AcceptanceWindowMinutes = policy.AcceptanceWindowMinutes,
            SettingsVersion = policy.Version,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 0,
        };

    /// <summary>LEAST(booked_at + scheduling.acceptanceWindowMinutes, starts_at − scheduling.acceptanceMinimumLeadMinutes) of the snapshot.</summary>
    public static DateTimeOffset Deadline(DateTimeOffset bookedAt, DateTimeOffset startsAt, SchedulingPolicySnapshot policy)
    {
        var byWindow = bookedAt.AddMinutes(policy.AcceptanceWindowMinutes);
        var byLead = startsAt.AddMinutes(-policy.AcceptanceMinimumLeadMinutes);
        return byWindow < byLead ? byWindow : byLead;
    }

    public static bool SameStart(AppointmentSnapshot appointment, DateTimeOffset requested) => appointment.StartsAt == requested.ToUniversalTime();

    public static void Cancel(TechnicianAppointment appointment, string reason, DateTimeOffset now)
    {
        appointment.Status = AppointmentStatuses.Cancelled;
        appointment.CancelledAt = now;
        appointment.CancellationReason = reason.Length <= 200 ? reason : reason[..200];
        appointment.UpdatedAt = now;
        appointment.Revision++;
    }
}

/// <summary>
/// Books a survey appointment immediately as PENDING_ACCEPTANCE: the capacity of one eligible technician of the
/// site's zone is held internally (never shown), the acceptance deadline is set, and technicians of the zone see it as
/// an open job. The survey request stays Pending until a technician is confirmed. A retry with the same start
/// returns the existing booking (idempotent).
/// </summary>
public sealed class BookSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    TechnicianEligibility eligibility,
    ZoneCoverageService zoneCoverage,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<BookingConfirmation>> HandleAsync(BookSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");

        // An open appointment whose start passed is dead (nobody accepted it in time): cancel it now (NOT_ACCEPTED_BEFORE_START,
        // as the escalation worker would) instead of blocking the new booking until the next sweep.
        if (context.ActiveAppointment is { } stale && AppointmentStatuses.IsOpen(stale.Status) && stale.StartsAt <= time.GetUtcNow())
        {
            await AppointmentEscalationService.TryCancelNotAcceptedAsync(unitOfWork, time, stale.Id, cancellationToken);
            context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
            if (context is null || context.CustomerUserId != command.CustomerUserId)
                return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        }

        if (context.ActiveAppointment is { } existing)
            return Appointments.SameStart(existing, command.StartsAt)
                ? SchedulingResult<BookingConfirmation>.Ok(Confirm(existing, created: false))
                : SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.BookingAlreadyExists,
                    "This survey request already has an active appointment. Reschedule or cancel it instead.");

        if (context.Status != SurveyRequestStatus.Pending)
            return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingState, "This survey request can no longer be booked.");

        // An open uncovered-zone dispatch request owns this request until it is resolved: accept its proposal or cancel it first.
        if (await unitOfWork.HasOpenDispatchRequestAsync(command.SurveyRequestId, cancellationToken))
            return OpenDispatch<BookingConfirmation>();

        var (window, code, reason) = await SlotGrid.ResolveAsync(unitOfWork, eligibility, calendar, context.PropertySiteId, command.StartsAt, time.GetUtcNow(), cancellationToken);
        if (window is null) return CapacityFailures.Fail<BookingConfirmation>(code!, reason!);
        var policy = await eligibility.PolicyAsync(cancellationToken);

        ReservationOutcome outcome;
        try
        {
            outcome = await reservations.ReserveAsync(
                new ReservationSpec(window.StartsAtUtc, window.EndsAtUtc, context.PropertySiteId, null, new HashSet<Guid>(), []),
                async (ctx, token) =>
                {
                    var current = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, token);
                    if (current is null || current.ActiveAppointment is not null)
                        return ApplyResult.Abort(SchedulingErrorCodes.BookingAlreadyExists, "This survey request already has an active appointment.");
                    if (current.Status != SurveyRequestStatus.Pending)
                        return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "This survey request can no longer be booked.");
                    // Under the site lock (a dispatch request is created under the same lock): no open dispatch request appeared meanwhile.
                    if (await unitOfWork.HasOpenDispatchRequestAsync(command.SurveyRequestId, token))
                        return ApplyResult.Abort(SchedulingErrorCodes.DispatchRequestOpen, OpenDispatchMessage);

                    var appointment = Appointments.New(context, window, ctx, command.CustomerUserId, command.Note, policy, ctx.NowUtc);
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

        // A concurrent identical request (e.g. a network retry) can commit first. Same start = the same booking.
        if (!outcome.Succeeded && outcome.ErrorCode == SchedulingErrorCodes.BookingAlreadyExists)
            return await ExistingOrConflictAsync(command, cancellationToken);
        if (!outcome.Succeeded && outcome.ErrorCode == SchedulingErrorCodes.SlotUnavailable)
            return await CapacityFailures.NoCapacityAsync<BookingConfirmation>(zoneCoverage, context.PropertySiteId, window, null, "", cancellationToken);
        if (!outcome.Succeeded)
            return CapacityFailures.Fail<BookingConfirmation>(outcome.ErrorCode!, outcome.Message!);

        var booked = await unitOfWork.GetAppointmentAsync(outcome.AppointmentId!.Value, cancellationToken);
        return SchedulingResult<BookingConfirmation>.Ok(Confirm(booked!, created: true));
    }

    internal const string OpenDispatchMessage =
        "This survey request has an open dispatch request: our staff are arranging a technician. Accept their proposal or cancel the dispatch request first.";

    internal static SchedulingResult<T> OpenDispatch<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.DispatchRequestOpen, OpenDispatchMessage);

    private async Task<SchedulingResult<BookingConfirmation>> ExistingOrConflictAsync(BookSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var winner = (await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken))?.ActiveAppointment;
        return winner is not null && Appointments.SameStart(winner, command.StartsAt)
            ? SchedulingResult<BookingConfirmation>.Ok(Confirm(winner, created: false))
            : SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.BookingAlreadyExists, "This survey request already has an active appointment.");
    }

    internal BookingConfirmation Confirm(AppointmentSnapshot a, bool created) => new(
        a.Id, a.SurveyRequestId!.Value,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
        a.Status, AcceptanceStatuses.Of(a.Status, a.StartsAt, a.AcceptanceDeadlineAt, time.GetUtcNow()),
        a.AcceptanceDeadlineAt is { } d ? TimeZoneInfo.ConvertTime(d, calendar.TimeZone) : null,
        created,
        created ? "Your survey appointment is booked. A technician serving your area will confirm it." : "This appointment was already booked.");
}

/// <summary>
/// Moves an open or confirmed future appointment to a new start (customer or Sales). The old row is cancelled
/// and linked to its replacement in the same transaction, so a failure leaves the original unchanged.
/// A confirmed technician keeps the job (same status and source) when still eligible at the new time — their own
/// pending holds that conflict with the new time are handed to other technicians in the same transaction
/// (<see cref="AppointmentAssignmentService.ReserveForTechnicianAsync"/>); otherwise the new appointment is
/// PENDING_ACCEPTANCE with a new holder and needs re-acceptance. Sales may also name a technician: the new appointment
/// is then ASSIGNED to them (reason required, ASSIGNED/REASSIGNED event). When the appointment only changed in the
/// background (a hold moved, a technician accepted, the deadline escalated) while it is still changeable at the same
/// start, the move is re-planned on the fresh row (bounded) instead of failing with a concurrency conflict.
/// </summary>
public sealed class AppointmentMover(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentReservationService reservations,
    AppointmentAssignmentService assignments,
    SchedulingOptions options,
    TimeProvider time)
{
    /// <param name="StillAllowed">Re-checked on the re-read context before a retry (ownership, lead time); null = only the generic checks.</param>
    public sealed record MoveSpec(
        SurveyRequestSchedulingContext Context,
        AppointmentSnapshot Current,
        AppointmentWindow Window,
        Guid ActorUserId,
        string? Note,
        string CancellationReason,
        string? EventReason,
        Guid? AssignTechnicianUserId,
        string? AssignmentReason,
        Func<SurveyRequestSchedulingContext, DateTimeOffset, bool>? StillAllowed = null);

    public async Task<SchedulingResult<AppointmentSnapshot>> MoveAsync(MoveSpec spec, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts); attempt++)
        {
            var moved = await MoveOnceAsync(spec, cancellationToken);
            if (moved.ErrorCode != ApplyResult.StaleCode) return moved;

            var context = await unitOfWork.GetSurveyRequestContextAsync(spec.Context.SurveyRequestId, cancellationToken);
            if (context?.ActiveAppointment is not { } fresh || !StillSameChange(spec, context, fresh)) break;
            spec = spec with { Context = context, Current = fresh };
        }
        return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed meanwhile. Reload and try again.");
    }

    /// <summary>A background change only: same row, same start, still changeable, and the caller's own rules still allow it.</summary>
    private bool StillSameChange(MoveSpec spec, SurveyRequestSchedulingContext context, AppointmentSnapshot fresh)
        => fresh.Id == spec.Current.Id && fresh.StartsAt == spec.Current.StartsAt && AppointmentStatuses.IsChangeable(fresh.Status)
           && fresh.StartsAt > time.GetUtcNow() && spec.StillAllowed?.Invoke(context, time.GetUtcNow()) != false;

    private async Task<SchedulingResult<AppointmentSnapshot>> MoveOnceAsync(MoveSpec spec, CancellationToken cancellationToken)
    {
        // Neither the time nor the confirmed technician may change once the on-site survey started (re-checked in the transaction).
        if (await OnSiteSurveyStartedAsync(unitOfWork, spec.Current, cancellationToken))
            return AppointmentAssignmentService.OnSiteSurveyStarted<AppointmentSnapshot>();
        var keep = spec.AssignTechnicianUserId
                   ?? (AppointmentStatuses.IsConfirmed(spec.Current.Status) ? spec.Current.AssignedTechnicianUserId : null);
        ReservationOutcome? outcome = null;
        try
        {
            if (keep is { } technician)
            {
                outcome = await assignments.ReserveForTechnicianAsync(Spec(spec, technician), spec.ActorUserId, Apply(spec, technician), cancellationToken);
                if (outcome.ErrorCode == ApplyResult.StaleCode) return Fail(outcome);
                if (!outcome.Succeeded && (spec.AssignTechnicianUserId is not null || !SchedulingErrorCodes.IsEligibilityCode(outcome.ErrorCode!)))
                    return SchedulingResult<AppointmentSnapshot>.Fail(outcome.ErrorCode!, outcome.Message! + " The original appointment is unchanged.");
            }
            if (outcome is not { Succeeded: true })
                outcome = await reservations.ReserveAsync(Spec(spec, null), Apply(spec, null), cancellationToken);
        }
        catch (SchedulingConflictException)
        {
            return SchedulingResult<AppointmentSnapshot>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed meanwhile. Reload and try again.");
        }

        if (outcome.ErrorCode == ApplyResult.StaleCode) return Fail(outcome);
        if (!outcome.Succeeded)
            return SchedulingResult<AppointmentSnapshot>.Fail(outcome.ErrorCode!, outcome.Message! + " The original appointment is unchanged.");
        return SchedulingResult<AppointmentSnapshot>.Ok((await unitOfWork.GetAppointmentAsync(outcome.AppointmentId!.Value, cancellationToken))!);
    }

    private static SchedulingResult<AppointmentSnapshot> Fail(ReservationOutcome outcome) => SchedulingResult<AppointmentSnapshot>.Fail(outcome.ErrorCode!, outcome.Message!);

    /// <summary>True when the request's on-site survey report is IN_PROGRESS (the confirmed technician is on site).</summary>
    internal static async Task<bool> OnSiteSurveyStartedAsync(ISchedulingUnitOfWork unitOfWork, AppointmentSnapshot appointment, CancellationToken token)
        => appointment.SurveyRequestId is { } requestId && await unitOfWork.HasOnSiteSurveyInProgressAsync(requestId, token);

    private static ReservationSpec Spec(MoveSpec spec, Guid? technician)
    {
        var current = spec.Current;
        var locks = new[] { current.ReservedTechnicianUserId, current.AssignedTechnicianUserId }.Where(id => id is not null).Select(id => id!.Value).ToList();
        return new ReservationSpec(spec.Window.StartsAtUtc, spec.Window.EndsAtUtc, current.PropertySiteId, technician, new HashSet<Guid> { current.Id }, locks);
    }

    private Func<ReservationContext, CancellationToken, Task<ApplyResult>> Apply(MoveSpec spec, Guid? technician) => async (ctx, token) =>
    {
        var current = spec.Current;
        var old = await unitOfWork.FindAppointmentForUpdateAsync(current.Id, token);
        if (old is null || !AppointmentStatuses.IsChangeable(old.Status))
            return ApplyResult.Abort(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed meanwhile. Reload and try again.");
        // Changed in the background since it was read (hold moved, accepted, escalated…): re-read and re-plan instead of failing.
        if (old.Revision != current.Revision) return ApplyResult.Stale();
        // Under the lock of the confirmed technician (always among the locked technicians): starting the survey takes the same lock.
        if (await OnSiteSurveyStartedAsync(unitOfWork, current, token))
            return ApplyResult.Abort(SchedulingErrorCodes.OnSiteSurveyInProgress,
                "The technician already started the on-site survey for this appointment; it cannot be moved now.");

        var previousStatus = old.Status;
        var previousTechnician = AppointmentStatuses.IsConfirmed(old.Status) ? old.AssignedTechnicianUserId : null;
        Appointments.Cancel(old, spec.CancellationReason, ctx.NowUtc);
        await unitOfWork.SaveChangesAsync(token);   // free the old interval before inserting the new one

        // A reschedule creates a NEW appointment: when it needs (re-)acceptance its deadline uses the settings current now.
        var next = Appointments.New(spec.Context, spec.Window, ctx, old.BookedByUserId, spec.Note ?? old.CustomerNote,
            await assignments.Eligibility.PolicyAsync(token), ctx.NowUtc);
        next.BookedAt = old.BookedAt;
        if (technician is not null)
        {
            next.AssignedTechnicianUserId = technician;
            // The technician keeps the job: no new acceptance is needed, so the stored deadline and its snapshot carry over unchanged.
            next.AcceptanceDeadlineAt = old.AcceptanceDeadlineAt;
            next.AcceptanceWindowMinutes = old.AcceptanceWindowMinutes;
            next.SettingsVersion = old.SettingsVersion;
            next.CrossZoneAuthorizationId = old.CrossZoneAuthorizationId;
            if (spec.AssignTechnicianUserId is not null)
            {
                next.Status = AppointmentStatuses.Assigned;
                next.AssignmentSource = AssignmentSources.SalesAssigned;
                next.AssignmentReason = spec.AssignmentReason;
                next.AssignedBySaleUserId = spec.ActorUserId;
                next.AssignedAt = ctx.NowUtc;
            }
            else
            {
                next.Status = previousStatus;
                next.AssignmentSource = old.AssignmentSource;
                next.AssignmentReason = old.AssignmentReason;
                next.AssignedBySaleUserId = old.AssignedBySaleUserId;
                next.AssignedAt = old.AssignedAt ?? ctx.NowUtc;
                next.AcceptedAt = old.AcceptedAt;
            }
        }
        unitOfWork.AddAppointment(next);
        old.ReplacedByAppointmentId = next.Id;

        if (!await unitOfWork.SetSurveyRequestScheduleAsync(spec.Context.SurveyRequestId, technician is null ? null : next.StartsAt, token))
            return ApplyResult.Abort(SchedulingErrorCodes.InvalidBookingState, "The survey request can no longer be rescheduled.");

        AppointmentHistory.Record(unitOfWork, next, AppointmentEventTypes.Rescheduled, spec.ActorUserId, ctx.NowUtc,
            previousTechnician ?? current.ReservedTechnicianUserId, next.AssignedTechnicianUserId ?? next.ReservedTechnicianUserId,
            old.StartsAt, next.StartsAt, spec.EventReason,
            notify: [previousTechnician, next.AssignedTechnicianUserId, spec.Context.AssignedSaleId == spec.ActorUserId ? null : spec.Context.AssignedSaleId,
                old.BookedByUserId == spec.ActorUserId ? null : old.BookedByUserId]);
        // Sales naming a technician is a staff assignment like assign-technician: ASSIGNED, or REASSIGNED when it replaces another confirmed technician.
        if (technician is { } named && spec.AssignTechnicianUserId is not null)
            AppointmentAssignmentService.RecordStaffAssignment(unitOfWork, next, AssignmentSources.SalesAssigned, spec.AssignmentReason, spec.ActorUserId, named,
                previousTechnician, current.ReservedTechnicianUserId, spec.Context.AssignedSaleId, ctx.NowUtc);
        return ApplyResult.Applied(next.Id);
    };
}

/// <summary>Customer reschedule: allowed while open or confirmed and at least <c>Scheduling:CustomerChangeLeadMinutes</c> before the start.</summary>
public sealed class RescheduleSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentMover mover,
    TechnicianEligibility eligibility,
    ZoneCoverageService zoneCoverage,
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
        var policy = await eligibility.PolicyAsync(cancellationToken);
        var guard = CustomerChangeGuard.Check(current, now, policy);
        if (guard is not null) return SchedulingResult<BookingConfirmation>.Fail(SchedulingErrorCodes.InvalidBookingState, guard);

        var confirmation = new BookingConfirmationFactory(calendar, time);
        if (Appointments.SameStart(current, command.StartsAt))
            return SchedulingResult<BookingConfirmation>.Ok(confirmation.Of(current, created: false, "The appointment already starts at this time."));
        if (await AppointmentMover.OnSiteSurveyStartedAsync(unitOfWork, current, cancellationToken))
            return AppointmentAssignmentService.OnSiteSurveyStarted<BookingConfirmation>();

        var (window, code, reason) = await SlotGrid.ResolveAsync(unitOfWork, eligibility, calendar, context.PropertySiteId, command.StartsAt, now, cancellationToken);
        if (window is null) return CapacityFailures.Fail<BookingConfirmation>(code!, reason!);

        var moved = await mover.MoveAsync(new AppointmentMover.MoveSpec(context, current, window, command.CustomerUserId, command.Note,
            CancellationReasons.Rescheduled, "CUSTOMER_RESCHEDULE", null, null,
            (fresh, at) => fresh.ActiveAppointment is { } a && CustomerChangeGuard.Allows(a, at, policy)), cancellationToken);
        if (!moved.Succeeded && moved.ErrorCode == SchedulingErrorCodes.SlotUnavailable)
            return await CapacityFailures.NoCapacityAsync<BookingConfirmation>(zoneCoverage, context.PropertySiteId, window, new HashSet<Guid> { current.Id },
                " The original appointment is unchanged.", cancellationToken);
        if (!moved.Succeeded) return CapacityFailures.Fail<BookingConfirmation>(moved.ErrorCode!, moved.Message!);

        var message = AppointmentStatuses.IsConfirmed(moved.Value!.Status)
            ? "Your survey appointment was rescheduled; your technician keeps the job."
            : "Your survey appointment was rescheduled. A technician serving your area will confirm the new time.";
        return SchedulingResult<BookingConfirmation>.Ok(confirmation.Of(moved.Value, created: true, message));
    }
}

internal sealed class BookingConfirmationFactory(WorkingCalendar calendar, TimeProvider time)
{
    public BookingConfirmation Of(AppointmentSnapshot a, bool created, string message) => new(
        a.Id, a.SurveyRequestId!.Value,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
        a.Status, AcceptanceStatuses.Of(a.Status, a.StartsAt, a.AcceptanceDeadlineAt, time.GetUtcNow()),
        a.AcceptanceDeadlineAt is { } d ? TimeZoneInfo.ConvertTime(d, calendar.TimeZone) : null,
        created, message);
}

/// <summary>
/// Cancels an open or confirmed future appointment and releases the capacity (history kept). A confirmed
/// request returns to Pending so the customer can book again. A revision change made in the background (hold moved,
/// accepted, escalated) while the appointment is still changeable at the same start is re-read and retried (bounded).
/// </summary>
public sealed class CancelSurveyAppointmentHandler(
    ISchedulingUnitOfWork unitOfWork,
    WorkingCalendar calendar,
    SchedulingOptions options,
    ISchedulingPolicyProvider policies,
    TimeProvider time)
{
    public async Task<SchedulingResult<CustomerAppointmentView>> HandleAsync(CancelSurveyAppointmentCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null || context.CustomerUserId != command.CustomerUserId)
            return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.ActiveAppointment is not { } current)
            return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.BookingNotFound, "There is no active appointment to cancel.");

        var policy = await policies.GetAsync(cancellationToken);
        var guard = CustomerChangeGuard.Check(current, time.GetUtcNow(), policy);
        if (guard is not null) return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.InvalidBookingState, guard);
        if (await AppointmentMover.OnSiteSurveyStartedAsync(unitOfWork, current, cancellationToken))
            return AppointmentAssignmentService.OnSiteSurveyStarted<CustomerAppointmentView>();

        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts); attempt++)
        {
            (TechnicianAppointment? Cancelled, bool Stale, bool SurveyStarted) outcome;
            try
            {
                var snapshot = current;
                outcome = await unitOfWork.ExecuteInTransactionAsync<(TechnicianAppointment?, bool, bool)>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync(new[] { snapshot.ReservedTechnicianUserId, snapshot.AssignedTechnicianUserId }
                        .Where(id => id is not null).Select(id => id!.Value), token);
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(snapshot.Id, token);
                    if (appointment is null || !AppointmentStatuses.IsChangeable(appointment.Status)) return ((null, false, false), false);
                    // Changed in the background (hold moved, accepted, escalated): re-read and decide again with the right locks.
                    if (appointment.Revision != snapshot.Revision) return ((null, true, false), false);
                    // Under the confirmed technician's lock: the on-site survey cannot have started meanwhile.
                    if (await AppointmentMover.OnSiteSurveyStartedAsync(unitOfWork, snapshot, token)) return ((null, false, true), false);

                    var now = time.GetUtcNow();
                    var confirmedTechnician = AppointmentStatuses.IsConfirmed(appointment.Status) ? appointment.AssignedTechnicianUserId : null;
                    Appointments.Cancel(appointment, string.IsNullOrWhiteSpace(command.Reason) ? CancellationReasons.CustomerCancelled : command.Reason.Trim(), now);
                    await unitOfWork.SetSurveyRequestScheduleAsync(context.SurveyRequestId, null, token);
                    AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.Cancelled, command.CustomerUserId, now,
                        previousTechnicianUserId: appointment.AssignedTechnicianUserId ?? appointment.ReservedTechnicianUserId, previousStartsAt: appointment.StartsAt,
                        reason: appointment.CancellationReason, notify: [confirmedTechnician, context.AssignedSaleId]);
                    await unitOfWork.SaveChangesAsync(token);
                    return ((appointment, false, false), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.StaleAppointment)
            {
                outcome = (null, true, false);
            }
            catch (SchedulingConflictException)
            {
                return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed. Reload and try again.");
            }

            if (outcome.Cancelled is { } cancelled)
                return SchedulingResult<CustomerAppointmentView>.Ok(CustomerViews.Appointment(AppointmentAssignmentService.Snapshot(cancelled), null,
                    calendar, policy, time.GetUtcNow()));
            if (outcome.SurveyStarted) return AppointmentAssignmentService.OnSiteSurveyStarted<CustomerAppointmentView>();
            if (!outcome.Stale) break;

            // Only a background change (same row, same start, still changeable by the customer) is retried.
            var fresh = (await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken))?.ActiveAppointment;
            if (fresh is null || fresh.Id != current.Id || fresh.StartsAt != current.StartsAt || !CustomerChangeGuard.Allows(fresh, time.GetUtcNow(), policy)) break;
            current = fresh;
        }

        return SchedulingResult<CustomerAppointmentView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed. Reload and try again.");
    }
}

/// <summary>Customers change open or confirmed appointments only until <c>scheduling.customerChangeLeadMinutes</c> (current settings) before the start.</summary>
internal static class CustomerChangeGuard
{
    public static string? Check(AppointmentSnapshot appointment, DateTimeOffset now, SchedulingPolicySnapshot policy)
    {
        if (!AppointmentStatuses.IsChangeable(appointment.Status))
            return "This appointment can no longer be changed.";
        if (appointment.StartsAt.AddMinutes(-policy.CustomerChangeLeadMinutes) <= now)
            return $"Appointments can be changed until {policy.CustomerChangeLeadMinutes} minutes before the start. Please contact our sales team.";
        return null;
    }

    public static bool Allows(AppointmentSnapshot appointment, DateTimeOffset now, SchedulingPolicySnapshot policy) => Check(appointment, now, policy) is null;
}

internal static class CustomerViews
{
    public static CustomerAppointmentView Appointment(AppointmentSnapshot a, string? technicianName, WorkingCalendar calendar, SchedulingPolicySnapshot policy, DateTimeOffset now)
    {
        var acceptance = AcceptanceStatuses.Of(a.Status, a.StartsAt, a.AcceptanceDeadlineAt, now);
        var confirmed = acceptance is AcceptanceStatuses.Confirmed or AcceptanceStatuses.Completed;
        return new CustomerAppointmentView(a.Id,
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone),
            TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status,
            acceptance,
            confirmed,
            confirmed ? technicianName : null,
            a.CustomerNote,
            a.BookedAt,
            CustomerChangeGuard.Allows(a, now, policy));
    }

    public static string Status(SurveyRequestStatus status) => status.ToString().ToUpperInvariant();
}

/// <summary>The customer's own survey requests with appointment summaries.</summary>
public sealed class CustomerSurveyRequestQueries(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, ISchedulingPolicyProvider policies, TimeProvider time)
{
    public async Task<IReadOnlyList<CustomerSurveyRequestView>> ListAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var policy = await policies.GetAsync(cancellationToken);
        return (await unitOfWork.ListCustomerSurveyRequestsAsync(customerUserId, cancellationToken)).Select(r => Map(r, policy, now)).ToList();
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
        var policy = await policies.GetAsync(cancellationToken);
        return SchedulingResult<CustomerSurveyRequestDetailView>.Ok(new CustomerSurveyRequestDetailView(Map(row, policy, time.GetUtcNow()), history));
    }

    private CustomerSurveyRequestView Map(CustomerSurveyRequestRow r, SchedulingPolicySnapshot policy, DateTimeOffset now) => new(
        r.SurveyRequestId, r.PreSurveyId, r.PropertySiteId, r.PropertyName, CustomerViews.Status(r.Status), r.SubmittedAt,
        r.ScheduledAt is { } s ? TimeZoneInfo.ConvertTime(s, calendar.TimeZone) : null,
        r.ActiveAppointment is { } a ? CustomerViews.Appointment(a, r.AssignedTechnicianName, calendar, policy, now) : null);
}
