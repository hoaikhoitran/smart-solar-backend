using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record AcceptAppointmentCommand(Guid TechnicianUserId, Guid AppointmentId, int? ExpectedRevision);

public sealed record ReleaseAppointmentCommand(Guid TechnicianUserId, Guid AppointmentId, string? Reason);

/// <summary>
/// Technician views. Confirmed jobs (ACCEPTED/ASSIGNED/COMPLETED) are visible in full only to the confirmed
/// technician; open jobs are visible to members of the site's operational zone with coarse location only.
/// Anything else is reported as not found, so other customers' data and appointments are never revealed.
/// </summary>
public sealed class TechnicianJobHandlers(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentAssignmentService assignments,
    TechnicianEligibility eligibility,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<IReadOnlyList<TechnicianAppointmentListItem>> ListAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await unitOfWork.ListTechnicianAppointmentsAsync(technicianUserId, time.GetUtcNow(), cancellationToken))
            .Select(x => new TechnicianAppointmentListItem(x.Appointment.Id, x.Appointment.AppointmentType,
                TimeZoneInfo.ConvertTime(x.Appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(x.Appointment.EndsAt, calendar.TimeZone),
                x.Appointment.Status, x.Appointment.AssignmentSource, x.PropertyName, x.Province, x.District, x.Appointment.AssignedAt))
            .ToList();

    public async Task<SchedulingResult<TechnicianJobView>> GetAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var job = await unitOfWork.GetTechnicianJobAsync(technicianUserId, appointmentId, cancellationToken);
        if (job is null)
            return SchedulingResult<TechnicianJobView>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");

        var a = job.Appointment;
        return SchedulingResult<TechnicianJobView>.Ok(new TechnicianJobView(
            a.Id, a.AppointmentType,
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status, a.AssignmentSource, a.AssignedAt, a.AcceptedAt, a.CustomerNote, job.SurveyRequestId, a.Revision,
            new TechnicianJobCustomer(job.CustomerName, job.CustomerPhone, job.CustomerEmail),
            new TechnicianJobProperty(job.PropertySiteId, job.PropertyName, job.Province, job.District, job.Ward, job.StreetLine,
                job.Latitude, job.Longitude, job.InstallationSurfaceType?.ToString(), job.SurfaceMaterial),
            new TechnicianJobPreSurvey(job.PreSurveyId, job.TotalAreaM2, job.UsableAreaM2, job.SurfaceLengthM, job.SurfaceWidthM,
                job.TiltDegree, job.AzimuthDegree, job.HasObstruction, SurfaceObstacleSerializer.Deserialize(job.ObstaclesJson)?.Count ?? 0),
            job.SelectedSimulation,
            job.SelectedSimulation is { } s ? $"/api/pre-surveys/{job.PreSurveyId}/simulations/{s.SimulationId}" : null));
    }

    /// <summary>Open future appointments in the technician's active zones, each with an advisory canAccept + reason.</summary>
    public async Task<IReadOnlyList<OpenAppointmentView>> ListOpenAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => await OpenAsync(technicianUserId, null, cancellationToken);

    /// <summary>One open appointment; not found when it is outside the technician's zones (no address leak) or no longer open.</summary>
    public async Task<SchedulingResult<OpenAppointmentView>> GetOpenAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
        => (await OpenAsync(technicianUserId, appointmentId, cancellationToken)).FirstOrDefault() is { } view
            ? SchedulingResult<OpenAppointmentView>.Ok(view)
            : SchedulingResult<OpenAppointmentView>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");

    private async Task<IReadOnlyList<OpenAppointmentView>> OpenAsync(Guid technicianUserId, Guid? appointmentId, CancellationToken cancellationToken)
    {
        var zones = await unitOfWork.ListTechnicianZonesAsync(technicianUserId, cancellationToken);
        if (zones.Count == 0) return [];
        var rows = await unitOfWork.ListOpenAppointmentsAsync(zones.Select(z => z.Id).ToList(), time.GetUtcNow(), appointmentId, cancellationToken);
        if (rows.Count == 0) return [];
        // Batched (constant query count whatever the number of rows): every site's coverage and the technician's days over the rows' dates.
        var coverages = await unitOfWork.GetSiteCoveragesAsync(rows.Select(r => r.Appointment.PropertySiteId).Distinct().ToList(), cancellationToken);
        var dates = rows.Select(r => calendar.LocalDateOf(r.Appointment.StartsAt)).ToList();
        var days = await eligibility.LoadDaysRangeAsync(dates.Min(), dates.Max(), new TechnicianScope([technicianUserId], SchedulableOnly: false), cancellationToken);
        var views = new List<OpenAppointmentView>();
        foreach (var row in rows)
        {
            var a = row.Appointment;
            var day = days.TryGetValue(calendar.LocalDateOf(a.StartsAt), out var ofDate) ? ofDate.SingleOrDefault() : null;
            var coverage = coverages[a.PropertySiteId];
            string? reason = EligibilityReasons.TechnicianInactive;
            var rehoming = false;
            if (day is not null)
            {
                var holds = day.Appointments.Where(s => s.IsPendingHold && s.AppointmentId != a.Id).Select(s => s.AppointmentId).Append(a.Id).ToHashSet();
                reason = eligibility.Evaluate(day, new EligibilityRequest(a.StartsAt, a.EndsAt, coverage.ZoneIds, holds));
                rehoming = reason is null && eligibility.Evaluate(day, new EligibilityRequest(a.StartsAt, a.EndsAt, coverage.ZoneIds, new HashSet<Guid> { a.Id })) is not null;
            }
            views.Add(new OpenAppointmentView(a.Id, a.SurveyRequestId!.Value,
                TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
                a.Status, a.AcceptanceDeadlineAt is { } d ? TimeZoneInfo.ConvertTime(d, calendar.TimeZone) : null, a.Revision,
                ZoneView.From(row.Zone), row.PropertyName, row.Province, row.District, row.Ward,
                row.InstallationSurfaceType?.ToString(), row.TotalAreaM2, row.UsableAreaM2, reason is null, reason, rehoming));
        }
        return views.OrderBy(v => v.StartsAt).ToList();
    }

    /// <summary>
    /// Self-acceptance. Repeating it (same technician) returns the unchanged appointment; when someone else was
    /// faster the result is 409 APPOINTMENT_ALREADY_TAKEN. Conflicting own pending holds are handed to other
    /// technicians in the same transaction, or the acceptance fails with TECHNICIAN_SCHEDULE_CONFLICT.
    /// </summary>
    public async Task<SchedulingResult<AssignmentView>> AcceptAsync(AcceptAppointmentCommand command, CancellationToken cancellationToken)
    {
        var result = await assignments.AssignAsync(new AssignmentSpec(command.AppointmentId, command.TechnicianUserId, command.TechnicianUserId,
            AssignmentSources.SelfAccepted, null, command.ExpectedRevision), cancellationToken);
        if (!result.Succeeded) return SchedulingResult<AssignmentView>.Fail(result.ErrorCode!, result.Message!, result.Details);
        var a = result.Value!.Appointment;
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        var request = await unitOfWork.GetSurveyRequestContextAsync(a.SurveyRequestId!.Value, cancellationToken);
        return SchedulingResult<AssignmentView>.Ok(new AssignmentView(a.Id, a.SurveyRequestId!.Value, command.TechnicianUserId, technician?.FullName ?? "",
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status, a.AssignmentSource, a.AssignmentReason, CustomerViews.Status(request!.Status), TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone),
            false, result.Value.Changed, a.Revision));
    }

    /// <summary>The confirmed technician can no longer attend: the appointment opens again (or escalates) and the request returns to Pending.</summary>
    public async Task<SchedulingResult<ReleaseView>> ReleaseAsync(ReleaseAppointmentCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500)
            return SchedulingResult<ReleaseView>.Fail(SchedulingErrorCodes.ValidationFailed, "A reason (at most 500 characters) is required.");
        var result = await assignments.ReleaseAsync(command.TechnicianUserId, command.AppointmentId, command.Reason.Trim(), cancellationToken);
        if (!result.Succeeded) return SchedulingResult<ReleaseView>.Fail(result.ErrorCode!, result.Message!);
        var a = result.Value!;
        return SchedulingResult<ReleaseView>.Ok(new ReleaseView(a.Id, a.Status, a.ReservedTechnicianUserId is not null,
            a.Status switch
            {
                AppointmentStatuses.Escalated => "Released. The appointment was escalated to staff.",
                AppointmentStatuses.Cancelled => "Released. The job went back to the Manager/Admin dispatch queue for reassignment.",
                _ => "Released. The appointment is open again for technicians of the zone.",
            }));
    }
}

/// <summary>
/// One technician-day for the technician or a staff dashboard: commitments in time order and potential
/// conflicts (previous end + buffer &gt; start). Staff views also show internal capacity holds.
/// </summary>
public sealed class TechnicianScheduleHandler(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, SmartSolar.Modules.SystemSettings.ISchedulingPolicyProvider policies)
{
    public async Task<TechnicianScheduleView> HandleAsync(Guid technicianUserId, DateOnly date, bool includeHolds, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = calendar.DayBoundsUtc(date);
        var entries = await unitOfWork.GetTechnicianScheduleAsync(technicianUserId, dayStart, dayEnd, includeHolds, cancellationToken);
        var bufferMinutes = (await policies.GetAsync(cancellationToken)).BufferBetweenAppointmentsMinutes;
        var buffer = TimeSpan.FromMinutes(bufferMinutes);

        var views = new List<ScheduleAppointmentView>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var previous = i > 0 ? entries[i - 1] : null;
            var conflict = previous is not null && previous.Appointment.EndsAt + buffer > entry.Appointment.StartsAt;
            views.Add(new ScheduleAppointmentView(
                entry.Appointment.Id, entry.Appointment.AppointmentType,
                TimeZoneInfo.ConvertTime(entry.Appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(entry.Appointment.EndsAt, calendar.TimeZone),
                entry.Appointment.Status, AppointmentStatuses.IsOpen(entry.Appointment.Status), entry.PropertyName, conflict));
        }

        return new TechnicianScheduleView(technicianUserId, date, calendar.TimeZone.Id, views.Count, bufferMinutes,
            views.Count(v => v.PotentialConflict), views,
            $"Consecutive commitments keep a fixed {bufferMinutes}-minute buffer; it is a conservative gap, not a travel-time estimate.");
    }
}
