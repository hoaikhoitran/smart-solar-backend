using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>What to reserve: the candidate interval and location on a local date, optionally a required technician.</summary>
public sealed record ReservationSpec(
    AppointmentCandidate Candidate,
    DateOnly LocalDate,
    Guid? RequiredTechnicianId,
    IReadOnlyList<Guid> AdditionalLockTechnicianIds,
    string EstimateContext);

/// <summary>Inside the locked transaction: the chosen technician and the verified routes.</summary>
public sealed record ReservationContext(TechnicianOption Option, TechnicianDayState LockedDay, DateTimeOffset NowUtc);

/// <summary>Result of the caller's write inside the reservation transaction.</summary>
public sealed record ApplyResult(bool Succeeded, Guid? AppointmentId, string? ErrorCode, string? Message)
{
    public static ApplyResult Applied(Guid appointmentId) => new(true, appointmentId, null, null);
    public static ApplyResult Abort(string code, string message) => new(false, null, code, message);
}

public sealed record ReservationOutcome(bool Succeeded, Guid? AppointmentId, TechnicianOption? Option, string? ErrorCode, string? Message, CandidateEvaluation? Evaluation)
{
    public static ReservationOutcome Fail(string code, string message, CandidateEvaluation? evaluation = null) => new(false, null, null, code, message, evaluation);
}

/// <summary>
/// Estimate-then-validate reservation. Provider calls happen with no transaction open; then, in a
/// short transaction, the technician rows are locked, the day is re-read and the plan re-checked
/// (same neighbours, same rules, same working schedule). Only then is the caller's write applied
/// and committed together with the new route snapshots (older snapshots of the same legs are
/// superseded). If the schedule changed, the transaction is rolled back and the whole step is
/// retried; database constraints remain the final guard against double booking.
/// </summary>
public sealed class AppointmentReservationService
{
    private readonly ISchedulingUnitOfWork _unitOfWork;
    private readonly TechnicianAvailabilityEvaluator _evaluator;
    private readonly ITravelTimeEstimator _estimator;
    private readonly SchedulingOptions _options;
    private readonly TimeProvider _time;
    private readonly TechnicianDayLoader _days;

    public AppointmentReservationService(
        ISchedulingUnitOfWork unitOfWork,
        TechnicianAvailabilityEvaluator evaluator,
        ITravelTimeEstimator estimator,
        SchedulingOptions options,
        TimeProvider time,
        WorkingCalendar calendar)
    {
        _unitOfWork = unitOfWork;
        _evaluator = evaluator;
        _estimator = estimator;
        _options = options;
        _time = time;
        _days = new TechnicianDayLoader(unitOfWork, calendar);
    }

    private enum Step { Done, Retry }

    public async Task<ReservationOutcome> ReserveAsync(
        ReservationSpec spec,
        Func<ReservationContext, CancellationToken, Task<ApplyResult>> apply,
        CancellationToken cancellationToken)
    {
        var budget = new RouteBudget(_estimator, _options.MaxRouteRequestsPerOperation);
        CandidateEvaluation? lastEvaluation = null;

        for (var attempt = 0; attempt < Math.Max(1, _options.MaxReservationAttempts); attempt++)
        {
            var days = await _days.LoadAsync(spec.LocalDate, spec.RequiredTechnicianId, cancellationToken);
            var evaluation = await _evaluator.EvaluateAsync(spec.Candidate, days, 1, budget, cancellationToken);
            lastEvaluation = evaluation;

            if (evaluation.Feasible.Count == 0)
            {
                var code = spec.RequiredTechnicianId is not null && !evaluation.HadUnverifiedRoutes
                    ? SchedulingErrorCodes.TechnicianNotAvailable
                    : evaluation.NoCapacityErrorCode();
                return ReservationOutcome.Fail(code, Describe(code, evaluation), evaluation);
            }

            var option = evaluation.Feasible[0];
            (Step Step, ApplyResult? Applied) result;
            try
            {
                result = await _unitOfWork.ExecuteInTransactionAsync<(Step, ApplyResult?)>(async token =>
                {
                    await _unitOfWork.LockTechniciansAsync(spec.AdditionalLockTechnicianIds.Append(option.Day.TechnicianUserId), token);

                    var fresh = (await _days.LoadAsync(spec.LocalDate, option.Day.TechnicianUserId, token)).SingleOrDefault();
                    var now = _time.GetUtcNow();
                    if (fresh is null
                        || FeasibilityRules.PreCheck(fresh, spec.Candidate, fresh.MaxAppointmentsPerDayOverride ?? _options.MaxAppointmentsPerTechnicianPerDay) is not null
                        || !SameNeighbours(FeasibilityRules.PlanLegs(fresh, spec.Candidate, _evaluator.SafetyBuffer, now), option.Plan)
                        || !FeasibilityRules.Evaluate(fresh, spec.Candidate, option.Plan, option.Inbound, option.Outbound, _evaluator.SafetyBuffer, now).IsFeasible)
                    {
                        return ((Step.Retry, null), false);
                    }

                    var applied = await apply(new ReservationContext(option, fresh, now), token);
                    if (!applied.Succeeded)
                    {
                        return ((Step.Done, applied), false);
                    }

                    var estimates = BuildEstimates(option, spec.Candidate, applied.AppointmentId!.Value, spec.EstimateContext).ToList();
                    await _unitOfWork.SupersedeEstimatesAsync(estimates.Select(e => e.AppointmentId).ToList(), now, token);
                    _unitOfWork.AddTravelEstimates(estimates);
                    await _unitOfWork.SaveChangesAsync(token);
                    return ((Step.Done, applied), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind is SchedulingConflictException.TechnicianOverlap or SchedulingConflictException.StaleAppointment)
            {
                // Another reservation won the race for this technician or appointment: re-estimate.
                continue;
            }

            if (result.Step == Step.Retry) continue;

            var outcome = result.Applied!;
            return outcome.Succeeded
                ? new ReservationOutcome(true, outcome.AppointmentId, option, null, null, evaluation)
                : ReservationOutcome.Fail(outcome.ErrorCode!, outcome.Message!, evaluation);
        }

        return ReservationOutcome.Fail(SchedulingErrorCodes.ConcurrencyConflict,
            "The technician schedule kept changing while the appointment was being reserved. Please try again.", lastEvaluation);
    }

    private static bool SameNeighbours(LegPlan fresh, LegPlan estimated)
        => Same(fresh.Previous, estimated.Previous) && Same(fresh.Next, estimated.Next);

    private static bool Same(ScheduledStop? a, ScheduledStop? b)
        => a is null ? b is null : b is not null && a.AppointmentId == b.AppointmentId && a.StartsAtUtc == b.StartsAtUtc && a.EndsAtUtc == b.EndsAtUtc && a.Location == b.Location;

    private IEnumerable<TechnicianTravelEstimate> BuildEstimates(TechnicianOption option, AppointmentCandidate candidate, Guid appointmentId, string context)
    {
        var buffer = _evaluator.SafetyBuffer;
        var inboundDeparture = option.Plan.Previous is null
            ? candidate.StartsAtUtc - buffer - option.Inbound.Duration!.Value
            : option.Plan.Inbound.DepartureAtUtc;
        // Route snapshots belong to survey appointments only. A neighbouring installation shift (Installation Flow)
        // is not a technician_appointment: its id is never stored as origin, and no snapshot is written for it.
        var inboundLeg = option.Plan.Previous is { CountsTowardDailyLimit: false } ? option.Plan.Inbound with { OriginAppointmentId = null } : option.Plan.Inbound;
        yield return Estimate(option.Day.TechnicianUserId, appointmentId, inboundLeg, option.Inbound, inboundDeparture, context);

        if (option.Plan.Outbound is { } outLeg && option.Outbound is { } outbound && outLeg.DestinationAppointmentId is { } nextId
            && option.Plan.Next is { CountsTowardDailyLimit: true })
        {
            yield return Estimate(option.Day.TechnicianUserId, nextId, outLeg with { OriginAppointmentId = appointmentId }, outbound, outLeg.DepartureAtUtc, context);
        }
    }

    private TechnicianTravelEstimate Estimate(Guid technicianId, Guid destinationAppointmentId, RequiredLeg leg, TravelEstimateResult result, DateTimeOffset departure, string context)
        => new()
        {
            Id = Guid.NewGuid(),
            AppointmentId = destinationAppointmentId,
            TechnicianUserId = technicianId,
            Context = context,
            OriginKind = leg.OriginKind,
            OriginAppointmentId = leg.OriginAppointmentId,
            OriginLatitude = leg.Origin.Latitude,
            OriginLongitude = leg.Origin.Longitude,
            DestinationLatitude = leg.Destination.Latitude,
            DestinationLongitude = leg.Destination.Longitude,
            DepartureAt = departure,
            EstimatedAt = result.EstimatedAtUtc,
            DistanceMeters = result.DistanceMeters is { } m ? Math.Round((decimal)m, 1) : null,
            DurationSeconds = result.Duration is { } d ? (int)Math.Ceiling(d.TotalSeconds) : null,
            SafetyBufferMinutes = _options.SafetyBufferMinutes,
            EstimatedArrivalAt = result.Duration is { } duration ? departure + duration : null,
            Status = result.Status,
            Provider = result.Provider,
            ProviderVersion = result.ProviderVersion,
            Vehicle = result.Vehicle,
            CongestionSummary = PreSurveyAppointments.TravelEstimateViews.Serialize(result.Congestion),
        };

    private static string Describe(string code, CandidateEvaluation evaluation) => code switch
    {
        SchedulingErrorCodes.TravelProviderQuotaExceeded => "Travel times cannot be verified right now because the routing quota is exhausted.",
        SchedulingErrorCodes.TravelProviderUnavailable => "Travel times cannot be verified right now because the routing provider is unavailable.",
        SchedulingErrorCodes.TravelRouteUnavailable => "No motorcycle route could be found to this location.",
        SchedulingErrorCodes.TechnicianNotAvailable => $"The technician cannot take this appointment ({string.Join(", ", evaluation.Rejected.Select(r => r.Reason).Distinct())}).",
        _ => "No technician can take this appointment time.",
    };
}
