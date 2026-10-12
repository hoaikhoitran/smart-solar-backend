using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.Services;

public static class RecalculationReasons
{
    public const string Cancellation = "CANCELLATION";
    public const string CustomerReschedule = "CUSTOMER_RESCHEDULE";
    public const string SalesReschedule = "SALES_RESCHEDULE";
    public const string Reassignment = "REASSIGNMENT";
    public const string BaseLocationChanged = "BASE_LOCATION_CHANGED";
    public const string WorkingScheduleChanged = "WORKING_SCHEDULE_CHANGED";
    public const string UnavailabilityChanged = "UNAVAILABILITY_CHANGED";
}

public enum RecalculationOutcome
{
    /// <summary>Nothing was due, or another worker holds the lease.</summary>
    NotDue,
    /// <summary>Every current leg is verified and stored; the request was removed.</summary>
    Completed,
    /// <summary>At least one leg could not be verified; the request is FAILED and will be retried.</summary>
    Failed,
}

/// <summary>
/// Re-estimates the inbound leg of every upcoming appointment of one technician-day whose stored
/// estimate no longer matches its real predecessor (the base, or the previous active appointment).
/// <list type="number">
/// <item>Lease the durable request (one worker at a time per technician-day).</item>
/// <item>Read the chain and call the provider with NO transaction open; legs whose current estimate
/// still matches origin, destination and predecessor are kept, so quota is only spent on changed legs.</item>
/// <item>In a short transaction, lock the technician and re-read the chain; if it changed, start over.</item>
/// <item>Supersede the old estimates of the recomputed legs and insert the new ones (failed legs are
/// stored with their failure status, so an invalidated ETA is never shown as current).</item>
/// <item>Delete the request when every leg is verified, otherwise mark it FAILED with exponential backoff.</item>
/// </list>
/// </summary>
public sealed class ScheduleRecalculationService(
    ISchedulingUnitOfWork unitOfWork,
    ITravelTimeEstimator estimator,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time,
    ILogger<ScheduleRecalculationService> logger)
{
    private const int MaxChainAttempts = 3;

    /// <summary>Runs a due request now, swallowing errors (the durable row guarantees a later retry).</summary>
    public async Task<RecalculationOutcome> TryRunAsync(Guid technicianUserId, DateOnly localDate, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(technicianUserId, localDate, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Route recalculation failed for technician {TechnicianId} on {Date}; it stays queued for retry.", technicianUserId, localDate);
            return RecalculationOutcome.Failed;
        }
    }

    public async Task<RecalculationOutcome> RunAsync(Guid technicianUserId, DateOnly localDate, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var lease = await unitOfWork.TryLeaseRecalculationAsync(technicianUserId, localDate, now,
            now.AddSeconds(Math.Max(10, options.RecalculationLeaseSeconds)), cancellationToken);
        if (lease is null) return RecalculationOutcome.NotDue;

        try
        {
            for (var attempt = 0; attempt < MaxChainAttempts; attempt++)
            {
                var chain = await ReadChainAsync(technicianUserId, localDate, cancellationToken);
                var plans = await EstimateAsync(chain, cancellationToken);

                var committed = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    await unitOfWork.LockTechniciansAsync([technicianUserId], token);
                    var fresh = await ReadChainAsync(technicianUserId, localDate, token);
                    if (fresh.Signature != chain.Signature) return (false, false);  // changed meanwhile: re-estimate

                    var stamp = time.GetUtcNow();
                    // Legs that could not even be requested (missing coordinates) still lose their old
                    // estimate: an invalidated ETA must never look current.
                    var changed = plans.Where(p => p.Result is not null || p.Failure is not null).Select(p => p.AppointmentId).ToList();
                    await unitOfWork.SupersedeEstimatesAsync(changed, stamp, token);
                    unitOfWork.AddTravelEstimates(plans.Where(p => p.Result is not null).Select(p => ToEstimate(technicianUserId, p)));
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);

                if (!committed) continue;

                var failures = plans.Where(p => p.Failure is not null || p.Result is { IsOk: false }).ToList();
                if (failures.Count == 0)
                {
                    await unitOfWork.CompleteRecalculationAsync(lease, cancellationToken);
                    logger.LogInformation("Recalculated {Legs} route leg(s) for technician {TechnicianId} on {Date}.",
                        plans.Count(p => p.Result is not null), technicianUserId, localDate);
                    return RecalculationOutcome.Completed;
                }

                var error = string.Join(",", failures.Select(f => f.Failure ?? f.Result!.Status).Distinct());
                await FailAsync(lease, error, cancellationToken);
                return RecalculationOutcome.Failed;
            }

            await FailAsync(lease, "SCHEDULE_KEPT_CHANGING", cancellationToken);
            return RecalculationOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(lease, "UNEXPECTED_ERROR", CancellationToken.None);
            throw;
        }
    }

    private Task FailAsync(RecalculationLease lease, string error, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var minutes = Math.Min(Math.Max(1, options.RecalculationMaxBackoffMinutes), Math.Pow(2, Math.Min(lease.Attempts, 10)));
        return unitOfWork.FailRecalculationAsync(lease, error, now.AddMinutes(minutes), now, cancellationToken);
    }

    private sealed record Chain(GeoPoint? Base, IReadOnlyList<ScheduleEntry> Entries, string Signature);

    private sealed record LegPlanItem(
        Guid AppointmentId, string OriginKind, Guid? OriginAppointmentId, GeoPoint? Origin, GeoPoint? Destination,
        DateTimeOffset StartsAt, DateTimeOffset RequestDeparture, TravelEstimateResult? Result, string? Failure);

    private async Task<Chain> ReadChainAsync(Guid technicianUserId, DateOnly localDate, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = calendar.DayBoundsUtc(localDate);
        var technician = await unitOfWork.GetTechnicianAsync(technicianUserId, cancellationToken);
        var baseLocation = technician?.Profile is { } p ? GeoPoint.From(p.BaseLatitude, p.BaseLongitude) : null;
        var entries = await unitOfWork.GetTechnicianScheduleAsync(technicianUserId, dayStart, dayEnd, cancellationToken);
        var signature = $"{baseLocation}|" + string.Join(";", entries.Select(e => $"{e.Appointment.Id}:{e.Appointment.StartsAt.UtcTicks}:{e.Appointment.EndsAt.UtcTicks}:{e.Location}"));
        return new Chain(baseLocation, entries, signature);
    }

    /// <summary>Provider calls for the legs that changed. Runs with no transaction open.</summary>
    private async Task<IReadOnlyList<LegPlanItem>> EstimateAsync(Chain chain, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var buffer = TimeSpan.FromMinutes(options.SafetyBufferMinutes);
        var budget = new RouteBudget(estimator, options.MaxRouteRequestsPerOperation);
        var plans = new List<LegPlanItem>();

        for (var i = 0; i < chain.Entries.Count; i++)
        {
            var entry = chain.Entries[i];
            if (entry.Appointment.StartsAt <= now) continue;   // already started: its travel is history

            var previous = i > 0 ? chain.Entries[i - 1] : null;
            var originKind = previous is null ? TravelOriginKinds.Base : TravelOriginKinds.Appointment;
            var origin = previous is null ? chain.Base : previous.Location;
            var departure = previous is null
                ? Max(entry.Appointment.StartsAt - buffer, now)
                : Max(previous.Appointment.EndsAt, now);

            var current = entry.LatestInboundEstimate;
            var stillValid = current is { Status: RouteCalculationStatuses.Ok }
                             && current.OriginKind == originKind
                             && current.OriginAppointmentId == previous?.Appointment.Id
                             && origin is { } o && current.OriginLatitude == o.Latitude && current.OriginLongitude == o.Longitude
                             && entry.Location is { } d && current.DestinationLatitude == d.Latitude && current.DestinationLongitude == d.Longitude;
            if (stillValid)
            {
                plans.Add(new LegPlanItem(entry.Appointment.Id, originKind, previous?.Appointment.Id, origin, entry.Location, entry.Appointment.StartsAt, departure, null, null));
                continue;
            }

            if (origin is null || entry.Location is null)
            {
                plans.Add(new LegPlanItem(entry.Appointment.Id, originKind, previous?.Appointment.Id, origin, entry.Location, entry.Appointment.StartsAt, departure, null,
                    "LOCATION_MISSING"));
                continue;
            }

            var result = await budget.EstimateAsync(new TravelEstimateRequest(origin.Value, entry.Location.Value, departure), cancellationToken);
            plans.Add(new LegPlanItem(entry.Appointment.Id, originKind, previous?.Appointment.Id, origin, entry.Location, entry.Appointment.StartsAt, departure, result, null));
        }

        return plans;
    }

    private TechnicianTravelEstimate ToEstimate(Guid technicianUserId, LegPlanItem plan)
    {
        var result = plan.Result!;
        var buffer = TimeSpan.FromMinutes(options.SafetyBufferMinutes);
        // From the base the technician leaves as late as possible; from an appointment, when it ends.
        var departure = plan.OriginKind == TravelOriginKinds.Base && result.Duration is { } d
            ? plan.StartsAt - buffer - d
            : plan.RequestDeparture;
        return new TechnicianTravelEstimate
        {
            Id = Guid.NewGuid(),
            AppointmentId = plan.AppointmentId,
            TechnicianUserId = technicianUserId,
            Context = TravelEstimateContexts.Recalculation,
            OriginKind = plan.OriginKind,
            OriginAppointmentId = plan.OriginAppointmentId,
            OriginLatitude = plan.Origin!.Value.Latitude,
            OriginLongitude = plan.Origin.Value.Longitude,
            DestinationLatitude = plan.Destination!.Value.Latitude,
            DestinationLongitude = plan.Destination.Value.Longitude,
            DepartureAt = departure,
            EstimatedAt = result.EstimatedAtUtc,
            DistanceMeters = result.IsOk && result.DistanceMeters is { } m ? Math.Round((decimal)m, 1) : null,
            DurationSeconds = result.IsOk && result.Duration is { } t ? (int)Math.Ceiling(t.TotalSeconds) : null,
            SafetyBufferMinutes = options.SafetyBufferMinutes,
            EstimatedArrivalAt = result.IsOk && result.Duration is { } duration ? departure + duration : null,
            Status = result.Status,
            Provider = result.Provider,
            ProviderVersion = result.ProviderVersion,
            Vehicle = result.Vehicle,
            CongestionSummary = result.IsOk ? TravelEstimateViews.Serialize(result.Congestion) : null,
        };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
