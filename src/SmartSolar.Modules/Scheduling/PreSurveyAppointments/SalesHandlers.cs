using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>Sales owner (IsManager = false) or Manager/Admin (true) asking for a request's technician availability.</summary>
public sealed record TechnicianAvailabilityQuery(Guid ActorUserId, bool IsManager, Guid SurveyRequestId);

/// <summary>Sales fallback assignment of the active appointment of an owned request.</summary>
public sealed record AssignTechnicianCommand(Guid SaleUserId, Guid SurveyRequestId, Guid TechnicianUserId, string? Reason, int? ExpectedRevision = null);

/// <summary>Manager/Admin override on an appointment (no ownership requirement).</summary>
public sealed record OverrideTechnicianCommand(Guid ManagerUserId, Guid AppointmentId, Guid TechnicianUserId, string? Reason, int? ExpectedRevision = null);

public sealed record SalesRescheduleCommand(Guid SaleUserId, Guid SurveyRequestId, DateTimeOffset StartsAt, string? Reason, Guid? TechnicianUserId);

public sealed record SetSalesOwnerCommand(Guid ActorUserId, Guid SurveyRequestId, Guid SaleUserId, string? Reason);

public sealed record SalesOwnerView(Guid SurveyRequestId, Guid SaleUserId, Guid? PreviousSaleUserId, bool Changed);

/// <summary>
/// Technicians who can take the appointment interval of a request (the appointment itself is ignored, so the
/// current holder/assignee is listed too), and every other member of the site's zone with the reason code
/// that makes them ineligible. A technician whose only obstacle is their own pending hold is eligible:
/// assigning them re-homes the hold (<see cref="AvailableTechnicianView.RequiresHoldRehoming"/>).
/// </summary>
public sealed class TechnicianAvailabilityHandler(ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, WorkingCalendar calendar)
{
    public async Task<SchedulingResult<TechnicianAvailabilityView>> HandleAsync(TechnicianAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(query.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (!query.IsManager && context.AssignedSaleId != query.ActorUserId)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.SurveyRequestNotOwned, "Claim this survey request first.");
        if (context.ActiveAppointment is not { } appointment)
            return SchedulingResult<TechnicianAvailabilityView>.Fail(SchedulingErrorCodes.BookingNotFound, "The customer has not booked an appointment yet.");

        var coverage = await unitOfWork.GetSiteCoverageAsync(appointment.PropertySiteId, cancellationToken);
        var (eligible, ineligible) = await EvaluateAsync(unitOfWork, eligibility, appointment, coverage, cancellationToken);
        return SchedulingResult<TechnicianAvailabilityView>.Ok(new TechnicianAvailabilityView(
            context.SurveyRequestId, appointment.Id,
            TimeZoneInfo.ConvertTime(appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(appointment.EndsAt, calendar.TimeZone),
            appointment.Status, appointment.Revision,
            ZoneView.From(coverage.Zone),
            eligible, ineligible));
    }

    internal static async Task<(IReadOnlyList<AvailableTechnicianView> Eligible, IReadOnlyList<IneligibleTechnicianView> Ineligible)> EvaluateAsync(
        ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, AppointmentSnapshot appointment, SiteCoverage coverage, CancellationToken cancellationToken)
    {
        var members = (await unitOfWork.ListZoneMemberIdsAsync(coverage.ZoneIds, cancellationToken)).ToHashSet();
        if (appointment.AssignedTechnicianUserId is { } assigned) members.Add(assigned);
        var date = eligibility.Calendar.LocalDateOf(appointment.StartsAt);
        var days = members.Count == 0 ? [] : await eligibility.LoadDaysAsync(date, new TechnicianScope(members.ToList(), SchedulableOnly: false), cancellationToken);
        return Evaluate(eligibility, appointment, coverage, days);
    }

    /// <summary>
    /// Members of the site's zone (plus the confirmed technician) as eligible/ineligible for the appointment interval, from their
    /// already loaded day states for the appointment's date (<paramref name="days"/> must contain exactly those technicians).
    /// </summary>
    internal static (IReadOnlyList<AvailableTechnicianView> Eligible, IReadOnlyList<IneligibleTechnicianView> Ineligible) Evaluate(
        TechnicianEligibility eligibility, AppointmentSnapshot appointment, SiteCoverage coverage, IEnumerable<TechnicianDayState> days)
    {
        var eligible = new List<AvailableTechnicianView>();
        var ineligible = new List<IneligibleTechnicianView>();
        foreach (var day in days.OrderBy(d => d.DisplayName).ThenBy(d => d.TechnicianUserId))
        {
            var holds = day.Appointments.Where(a => a.IsPendingHold && a.AppointmentId != appointment.Id).Select(a => a.AppointmentId);
            var ignore = holds.Append(appointment.Id).ToHashSet();
            var reason = eligibility.Evaluate(day, new EligibilityRequest(appointment.StartsAt, appointment.EndsAt, coverage.ZoneIds, ignore));
            if (reason is not null)
            {
                ineligible.Add(new IneligibleTechnicianView(day.TechnicianUserId, day.DisplayName, reason));
                continue;
            }
            var strict = eligibility.Evaluate(day, new EligibilityRequest(appointment.StartsAt, appointment.EndsAt, coverage.ZoneIds, new HashSet<Guid> { appointment.Id }));
            eligible.Add(new AvailableTechnicianView(day.TechnicianUserId, day.DisplayName,
                appointment.ReservedTechnicianUserId == day.TechnicianUserId, appointment.AssignedTechnicianUserId == day.TechnicianUserId,
                day.CountedCommitments(new HashSet<Guid> { appointment.Id }), strict is not null));
        }
        return (eligible.OrderByDescending(e => e.IsAssigned).ThenByDescending(e => e.IsCapacityHolder).ThenBy(e => e.CommitmentsThatDay).ToList(), ineligible);
    }
}

/// <summary>
/// Staff fallback: the Sales owner (or a Manager/Admin override) assigns a technician to an open or confirmed
/// future appointment, with a mandatory reason. Same eligibility, hold re-homing and revision-checked update as
/// self-acceptance; replacing a confirmed technician records REASSIGNED with the old and new technician.
/// </summary>
public sealed class AssignTechnicianHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentAssignmentService assignments,
    WorkingCalendar calendar)
{
    public async Task<SchedulingResult<AssignmentView>> HandleAsync(AssignTechnicianCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.AssignedSaleId != command.SaleUserId)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.SurveyRequestNotOwned, "Only the Sales owner can assign a technician. Claim this survey request first.");
        if (context.ActiveAppointment is not { } appointment)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.BookingNotFound, "The customer has not booked an appointment yet.");

        // The ownership check above is advisory; it is repeated inside the assignment transaction (the owner may change meanwhile).
        return await AssignAsync(new AssignmentSpec(appointment.Id, command.TechnicianUserId, command.SaleUserId, AssignmentSources.SalesAssigned,
            command.Reason, command.ExpectedRevision, RequiredSalesOwnerId: command.SaleUserId), cancellationToken);
    }

    public async Task<SchedulingResult<AssignmentView>> OverrideAsync(OverrideTechnicianCommand command, CancellationToken cancellationToken)
    {
        var appointment = await unitOfWork.GetAppointmentAsync(command.AppointmentId, cancellationToken);
        if (appointment is null || appointment.SurveyRequestId is null)
            return SchedulingResult<AssignmentView>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");
        return await AssignAsync(new AssignmentSpec(appointment.Id, command.TechnicianUserId, command.ManagerUserId, AssignmentSources.ManagerOverride,
            command.Reason, command.ExpectedRevision), cancellationToken);
    }

    private async Task<SchedulingResult<AssignmentView>> AssignAsync(AssignmentSpec spec, CancellationToken cancellationToken)
    {
        var result = await assignments.AssignAsync(spec, cancellationToken);
        if (!result.Succeeded) return SchedulingResult<AssignmentView>.Fail(result.ErrorCode!, result.Message!, result.Details);
        var a = result.Value!.Appointment;
        var technician = await unitOfWork.GetTechnicianAsync(a.AssignedTechnicianUserId!.Value, cancellationToken);
        var request = await unitOfWork.GetSurveyRequestContextAsync(a.SurveyRequestId!.Value, cancellationToken);
        return SchedulingResult<AssignmentView>.Ok(new AssignmentView(
            a.Id, a.SurveyRequestId!.Value, a.AssignedTechnicianUserId!.Value, technician?.FullName ?? "",
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status, a.AssignmentSource, a.AssignmentReason, CustomerViews.Status(request!.Status),
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone),
            result.Value.Changed && result.Value.PreviousTechnicianUserId is { } previous && previous != a.AssignedTechnicianUserId,
            result.Value.Changed, a.Revision));
    }
}

/// <summary>
/// The staff queue: ESCALATED appointments and PENDING_ACCEPTANCE ones past their deadline (worker-independent),
/// future only, with the zone and whether any technician could still take them.
/// </summary>
public sealed class EscalatedAppointmentsHandler(ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, WorkingCalendar calendar, TimeProvider time)
{
    /// <summary>A constant number of queries whatever the number of rows: coverage, memberships and technician days are loaded in batches.</summary>
    public async Task<IReadOnlyList<EscalatedAppointmentView>> HandleAsync(Guid saleUserId, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.ListEscalatedAppointmentsAsync(time.GetUtcNow(), cancellationToken);
        if (rows.Count == 0) return [];

        var coverages = await unitOfWork.GetSiteCoveragesAsync(rows.Select(r => r.Appointment.PropertySiteId).Distinct().ToList(), cancellationToken);
        var memberships = await unitOfWork.ListZoneMembershipsAsync(coverages.Values.SelectMany(c => c.ZoneIds).Distinct().ToList(), cancellationToken);
        var membersOf = rows.ToDictionary(r => r.Appointment.Id, r =>
        {
            var zones = coverages[r.Appointment.PropertySiteId].ZoneIds;
            var ids = memberships.Where(m => zones.Contains(m.ZoneId)).Select(m => m.TechnicianUserId).ToHashSet();
            if (r.Appointment.AssignedTechnicianUserId is { } assigned) ids.Add(assigned);
            return ids;
        });
        var everyone = membersOf.Values.SelectMany(ids => ids).Distinct().ToList();
        var dates = rows.Select(r => calendar.LocalDateOf(r.Appointment.StartsAt)).ToList();
        var daysByDate = everyone.Count == 0
            ? new Dictionary<DateOnly, IReadOnlyList<TechnicianDayState>>()
            : await eligibility.LoadDaysRangeAsync(dates.Min(), dates.Max(), new TechnicianScope(everyone, SchedulableOnly: false), cancellationToken);

        var views = new List<EscalatedAppointmentView>();
        foreach (var row in rows)
        {
            var a = row.Appointment;
            var coverage = coverages[a.PropertySiteId];
            var members = membersOf[a.Id];
            var days = daysByDate.TryGetValue(calendar.LocalDateOf(a.StartsAt), out var all) ? all.Where(d => members.Contains(d.TechnicianUserId)) : [];
            var (eligible, _) = TechnicianAvailabilityHandler.Evaluate(eligibility, a, coverage, days);
            views.Add(new EscalatedAppointmentView(a.Id, a.SurveyRequestId!.Value,
                TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
                a.Status,
                a.AcceptanceDeadlineAt is { } d ? TimeZoneInfo.ConvertTime(d, calendar.TimeZone) : null,
                a.EscalatedAt is { } e ? TimeZoneInfo.ConvertTime(e, calendar.TimeZone) : null,
                a.Revision, ZoneView.From(row.Zone), row.CustomerName, row.PropertyName, row.Province, row.District, row.Ward,
                row.AssignedSaleId, row.AssignedSaleId == saleUserId, eligible.Count > 0));
        }
        return views.OrderBy(v => v.StartsAt).ToList();
    }
}

/// <summary>
/// Sales-managed rescheduling of an owned request. The new time is re-validated: the confirmed technician keeps
/// the job when still eligible; passing technicianUserId assigns that technician (reason required). The old
/// appointment is cancelled (RESCHEDULED_BY_SALES) and replaced in one transaction.
/// </summary>
public sealed class SalesRescheduleHandler(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentMover mover,
    TechnicianEligibility eligibility,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<SchedulingResult<SalesRescheduleView>> HandleAsync(SalesRescheduleCommand command, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetSurveyRequestContextAsync(command.SurveyRequestId, cancellationToken);
        if (context is null)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found.");
        if (context.AssignedSaleId != command.SaleUserId)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.SurveyRequestNotOwned, "Only the Sales owner can reschedule. Claim this survey request first.");
        if (context.ActiveAppointment is not { } current)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.BookingNotFound, "There is no active appointment to reschedule.");

        var now = time.GetUtcNow();
        if (!AppointmentStatuses.IsChangeable(current.Status))
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.InvalidBookingState, "Completed appointments cannot be rescheduled.");
        if (current.StartsAt <= now)
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.InvalidBookingState, "Past or ongoing appointments cannot be rescheduled.");
        if (command.TechnicianUserId is not null && string.IsNullOrWhiteSpace(command.Reason))
            return SchedulingResult<SalesRescheduleView>.Fail(SchedulingErrorCodes.AssignmentReasonRequired, "A reason is required when assigning a technician.");

        if (Appointments.SameStart(current, command.StartsAt) && command.TechnicianUserId is null)
            return SchedulingResult<SalesRescheduleView>.Ok(await ViewAsync(current, null, cancellationToken));
        // The technician is on site: neither the time nor the technician may change (re-checked under the locks by the mover).
        if (await AppointmentMover.OnSiteSurveyStartedAsync(unitOfWork, current, cancellationToken))
            return AppointmentAssignmentService.OnSiteSurveyStarted<SalesRescheduleView>();

        var (window, code, reason) = await SlotGrid.ResolveAsync(unitOfWork, eligibility, calendar, context.PropertySiteId, command.StartsAt, now, cancellationToken);
        if (window is null) return SchedulingResult<SalesRescheduleView>.Fail(code!, reason!);

        var moved = await mover.MoveAsync(new AppointmentMover.MoveSpec(context, current, window, command.SaleUserId, null,
            CancellationReasons.RescheduledBySales, command.Reason?.Trim() ?? "SALES_RESCHEDULE", command.TechnicianUserId, command.Reason?.Trim(),
            // Retried only after background changes (hold moved, escalation): a concurrent (re)assignment or acceptance is a decision
            // Sales did not see, so exactly one of the two wins and this one reports a conflict.
            (fresh, _) => fresh.AssignedSaleId == command.SaleUserId && fresh.ActiveAppointment is { } a
                          && a.AssignedTechnicianUserId == current.AssignedTechnicianUserId && AppointmentStatuses.IsConfirmed(a.Status) == AppointmentStatuses.IsConfirmed(current.Status)),
            cancellationToken);
        return moved.Succeeded
            ? SchedulingResult<SalesRescheduleView>.Ok(await ViewAsync(moved.Value!, current.Id, cancellationToken))
            : SchedulingResult<SalesRescheduleView>.Fail(moved.ErrorCode!, moved.Message!);
    }

    private async Task<SalesRescheduleView> ViewAsync(AppointmentSnapshot a, Guid? previousId, CancellationToken cancellationToken)
    {
        var technician = a.AssignedTechnicianUserId is { } id ? await unitOfWork.GetTechnicianAsync(id, cancellationToken) : null;
        var request = await unitOfWork.GetSurveyRequestContextAsync(a.SurveyRequestId!.Value, cancellationToken);
        return new SalesRescheduleView(a.Id, previousId, a.SurveyRequestId!.Value, a.AssignedTechnicianUserId, technician?.FullName,
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status, AcceptanceStatuses.Of(a.Status, a.StartsAt, a.AcceptanceDeadlineAt, time.GetUtcNow()), CustomerViews.Status(request!.Status),
            AppointmentStatuses.IsConfirmed(a.Status) ? TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone) : null);
    }
}

/// <summary>
/// Manager/Admin sets the commercial (Sales) owner of a request; the target must be an active SALES user. Audited with the
/// owner that was really replaced (locked/conditional update); a concurrent claim is re-read and retried, then 409
/// SURVEY_REQUEST_UNAVAILABLE.
/// </summary>
public sealed class SalesOwnerHandler(ISchedulingUnitOfWork unitOfWork, TimeProvider time)
{
    public async Task<SchedulingResult<SalesOwnerView>> HandleAsync(SetSalesOwnerCommand command, CancellationToken cancellationToken)
    {
        if (!await unitOfWork.IsActiveUserWithRoleAsync(command.SaleUserId, [RoleCodes.Sales], cancellationToken))
            return SchedulingResult<SalesOwnerView>.Fail(SchedulingErrorCodes.SalesUserInvalid, "The new owner must be an active SALES user.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<SalesOwnerView>?>(async token =>
            {
                var now = time.GetUtcNow();
                var update = await unitOfWork.SetSalesOwnerAsync(command.SurveyRequestId, command.SaleUserId, now, token);
                if (!update.Found) return (SchedulingResult<SalesOwnerView>.Fail(SchedulingErrorCodes.SurveyRequestNotFound, "Survey request was not found."), false);
                if (!update.Updated) return (null, false);   // a concurrent claim/owner change won: re-read the true previous owner
                var previous = update.PreviousSaleId;
                if (previous == command.SaleUserId)
                    return (SchedulingResult<SalesOwnerView>.Ok(new SalesOwnerView(command.SurveyRequestId, command.SaleUserId, previous, false)), false);

                unitOfWork.AddSurveyRequestEvent(new SurveyRequestEvent
                {
                    Id = Guid.NewGuid(), SurveyRequestId = command.SurveyRequestId, EventType = SurveyRequestEventTypes.SalesOwnerChanged,
                    ActorUserId = command.ActorUserId, PreviousSaleUserId = previous, NewSaleUserId = command.SaleUserId,
                    Reason = command.Reason!.Trim(), CreatedAt = now,
                });
                await unitOfWork.SaveChangesAsync(token);
                return (SchedulingResult<SalesOwnerView>.Ok(new SalesOwnerView(command.SurveyRequestId, command.SaleUserId, previous, true)), true);
            }, cancellationToken);
            if (result is not null) return result;
        }
        return SchedulingResult<SalesOwnerView>.Fail(PreSurveyErrorCodes.SurveyRequestUnavailable,
            "The survey request owner kept changing. Reload and try again.");
    }
}
