using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>
/// Technician views of officially assigned jobs only. Anything not assigned to the caller is
/// reported as not found, so other customers' data and appointments are never revealed.
/// </summary>
public sealed class TechnicianJobHandlers(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, TimeProvider time)
{
    public async Task<IReadOnlyList<TechnicianAppointmentListItem>> ListAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await unitOfWork.ListTechnicianAppointmentsAsync(technicianUserId, time.GetUtcNow(), cancellationToken))
            .Select(x => new TechnicianAppointmentListItem(x.Appointment.Id, x.Appointment.AppointmentType,
                TimeZoneInfo.ConvertTime(x.Appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(x.Appointment.EndsAt, calendar.TimeZone),
                x.Appointment.Status, x.PropertyName, x.Province, x.District, x.Appointment.AcknowledgedAt))
            .ToList();

    public async Task<SchedulingResult<TechnicianJobView>> GetAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var job = await unitOfWork.GetTechnicianJobAsync(technicianUserId, appointmentId, cancellationToken);
        if (job is null)
            return SchedulingResult<TechnicianJobView>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found.");

        var estimate = await unitOfWork.GetLatestInboundEstimateAsync(appointmentId, cancellationToken);
        var a = job.Appointment;
        return SchedulingResult<TechnicianJobView>.Ok(new TechnicianJobView(
            a.Id, a.AppointmentType,
            TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone),
            a.Status, a.AssignedAt, a.AcknowledgedAt, a.CustomerNote, job.SurveyRequestId,
            new TechnicianJobCustomer(job.CustomerName, job.CustomerPhone, job.CustomerEmail),
            new TechnicianJobProperty(job.PropertySiteId, job.PropertyName, job.Province, job.District, job.Ward, job.StreetLine,
                job.Latitude, job.Longitude, job.InstallationSurfaceType?.ToString(), job.SurfaceMaterial),
            new TechnicianJobPreSurvey(job.PreSurveyId, job.TotalAreaM2, job.UsableAreaM2, job.SurfaceLengthM, job.SurfaceWidthM,
                job.TiltDegree, job.AzimuthDegree, job.HasObstruction, SurfaceObstacleSerializer.Deserialize(job.ObstaclesJson)?.Count ?? 0),
            job.SelectedSimulation,
            job.SelectedSimulation is { } s ? $"/api/pre-surveys/{job.PreSurveyId}/simulations/{s.SimulationId}" : null,
            estimate is null ? null : TravelEstimateViews.From(estimate)));
    }

    /// <summary>Records that the assigned job was received. Repeating it returns the original acknowledgment.</summary>
    public async Task<SchedulingResult<AcknowledgementView>> AcknowledgeAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<AcknowledgementView>>(async token =>
                {
                    var appointment = await unitOfWork.FindAppointmentForUpdateAsync(appointmentId, token);
                    if (appointment is null || appointment.AssignedTechnicianUserId != technicianUserId
                        || appointment.Status is not (AppointmentStatuses.Assigned or AppointmentStatuses.Acknowledged))
                        return (SchedulingResult<AcknowledgementView>.Fail(SchedulingErrorCodes.AppointmentNotFound, "Appointment was not found."), false);

                    if (appointment.Status == AppointmentStatuses.Acknowledged)
                        return (SchedulingResult<AcknowledgementView>.Ok(new AcknowledgementView(appointment.Id, appointment.Status, appointment.AcknowledgedAt!.Value, true)), false);

                    var now = time.GetUtcNow();
                    appointment.Status = AppointmentStatuses.Acknowledged;
                    appointment.AcknowledgedAt = now;
                    appointment.UpdatedAt = now;
                    appointment.Revision++;
                    Services.AppointmentHistory.Record(unitOfWork, appointment, Entities.AppointmentEventTypes.Acknowledged, technicianUserId, now,
                        newTechnicianUserId: technicianUserId, notify: [appointment.AssignedBySaleUserId]);
                    await unitOfWork.SaveChangesAsync(token);
                    return (SchedulingResult<AcknowledgementView>.Ok(new AcknowledgementView(appointment.Id, appointment.Status, now, false)), true);
                }, cancellationToken);
                return result;
            }
            catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.StaleAppointment)
            {
                // A concurrent acknowledgment (or change) won; re-read and answer idempotently.
            }
        }

        return SchedulingResult<AcknowledgementView>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The appointment changed. Try again.");
    }
}

public static class RoutingStatuses
{
    /// <summary>The stored estimate describes the current predecessor and was verified.</summary>
    public const string Ok = "OK";
    /// <summary>The chain changed and a recalculation is queued or running; the old value is not shown as current.</summary>
    public const string PendingRecalculation = "PENDING_RECALCULATION";
    /// <summary>The estimate's origin no longer matches the predecessor and nothing is queued.</summary>
    public const string Stale = "STALE";
    /// <summary>The latest attempt to verify this leg failed (provider error, no route, missing coordinates).</summary>
    public const string Failed = "FAILED";
    /// <summary>No estimate exists for this leg.</summary>
    public const string Missing = "MISSING";
}

/// <summary>
/// One technician-day for dashboards and planning: active appointments, the current inbound route
/// snapshot of each (a historical estimate, not live traffic), totals and potential conflicts. No
/// provider call is made here. Only snapshots that are not superseded and whose origin matches the
/// real predecessor count as current; everything else is labelled (PENDING_RECALCULATION, STALE,
/// FAILED, MISSING) and excluded from the travel totals, so old and new legs are never double-counted.
/// </summary>
public sealed class TechnicianScheduleHandler(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, SchedulingOptions options, TimeProvider time)
{
    public async Task<TechnicianScheduleView> HandleAsync(Guid technicianUserId, DateOnly date, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = calendar.DayBoundsUtc(date);
        var entries = await unitOfWork.GetTechnicianScheduleAsync(technicianUserId, dayStart, dayEnd, cancellationToken);
        var recalculation = await unitOfWork.GetRecalculationAsync(technicianUserId, date, cancellationToken);
        var technician = await unitOfWork.GetTechnicianAsync(technicianUserId, cancellationToken);
        var baseLocation = technician?.Profile is { } profile ? GeoPoint.From(profile.BaseLatitude, profile.BaseLongitude) : null;
        var earliestDeparture = calendar.ToUtc(date, technician?.Profile?.EarliestBaseDeparture ?? calendar.DefaultEarliestBaseDeparture);
        var buffer = TimeSpan.FromMinutes(options.SafetyBufferMinutes);
        var now = time.GetUtcNow();

        var views = new List<ScheduleAppointmentView>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var previous = i > 0 ? entries[i - 1] : null;
            var estimate = entry.LatestInboundEstimate;
            var originMatches = estimate is not null && (previous is null
                ? estimate.OriginKind == TravelOriginKinds.Base && baseLocation is { } b && estimate.OriginLatitude == b.Latitude && estimate.OriginLongitude == b.Longitude
                : estimate.OriginKind == TravelOriginKinds.Appointment && estimate.OriginAppointmentId == previous.Appointment.Id);

            var routing = estimate switch
            {
                null => recalculation is not null ? RoutingStatuses.PendingRecalculation : RoutingStatuses.Missing,
                _ when !originMatches => recalculation is not null ? RoutingStatuses.PendingRecalculation : RoutingStatuses.Stale,
                { Status: not Travel.RouteCalculationStatuses.Ok } => RoutingStatuses.Failed,
                _ => RoutingStatuses.Ok,
            };

            var conflict = previous is not null && previous.Appointment.EndsAt > entry.Appointment.StartsAt;
            if (routing == RoutingStatuses.Ok && estimate!.DurationSeconds is { } seconds)
            {
                var travel = TimeSpan.FromSeconds(seconds);
                conflict |= previous is null
                    ? entry.Appointment.StartsAt - buffer - travel < earliestDeparture
                    : previous.Appointment.EndsAt + travel + buffer > entry.Appointment.StartsAt;
            }

            views.Add(new ScheduleAppointmentView(
                entry.Appointment.Id, entry.Appointment.AppointmentType,
                TimeZoneInfo.ConvertTime(entry.Appointment.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(entry.Appointment.EndsAt, calendar.TimeZone),
                entry.Appointment.Status, entry.PropertyName, entry.Location?.Latitude, entry.Location?.Longitude,
                previous is null ? TravelOriginKinds.Base : TravelOriginKinds.Appointment,
                previous?.Appointment.Id,
                routing == RoutingStatuses.Ok ? TravelEstimateViews.From(estimate!) : null,
                estimate is null || routing == RoutingStatuses.Ok ? null : TravelEstimateViews.From(estimate),
                routing,
                estimate is null ? null : (int)Math.Max(0, Math.Floor((now - estimate.EstimatedAt).TotalMinutes)),
                conflict));
        }

        var ok = views.Where(v => v.InboundTravel is not null).Select(v => v.InboundTravel!).ToList();
        return new TechnicianScheduleView(
            technicianUserId, date, calendar.TimeZone.Id, views.Count,
            ok.Sum(e => e.DistanceMeters ?? 0), ok.Sum(e => e.DurationSeconds ?? 0), ok.Count * options.SafetyBufferMinutes,
            views.Count(v => v.PotentialConflict), views.Count(v => v.RoutingStatus != RoutingStatuses.Ok),
            TimeZoneInfo.ConvertTime(earliestDeparture, calendar.TimeZone),
            recalculation is null ? null : new RecalculationStateView(recalculation.Status, recalculation.Reason, recalculation.RequestedAt,
                recalculation.Attempts, recalculation.NextAttemptAt, recalculation.LastError),
            views,
            "Travel values are stored motorcycle estimates (Vietmap Route v4) made when the route chain last changed; they are not live traffic. "
            + "Totals include only legs with routingStatus OK.");
    }
}
