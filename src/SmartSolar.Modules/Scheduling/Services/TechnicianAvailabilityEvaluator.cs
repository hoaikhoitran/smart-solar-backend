using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>A technician who can take the candidate, with the route estimates that prove it.</summary>
public sealed record TechnicianOption(
    TechnicianDayState Day,
    LegPlan Plan,
    TravelEstimateResult Inbound,
    TravelEstimateResult? Outbound,
    FeasibilityResult Result);

public sealed record TechnicianRejection(Guid TechnicianUserId, string Reason, string? RouteStatus);

/// <summary>
/// <see cref="Exhaustive"/> is true only when every technician that passed the pre-checks was
/// route-checked with a verified answer and the search did not stop early, so
/// <see cref="Feasible"/>.Count is the exact number of technicians who can take the slot.
/// Otherwise the count is only a lower bound.
/// </summary>
public sealed record CandidateEvaluation(IReadOnlyList<TechnicianOption> Feasible, IReadOnlyList<TechnicianRejection> Rejected, bool Exhaustive = true)
{
    /// <summary>
    /// Error code explaining why nothing is feasible. Unverified routes take precedence over plain
    /// unavailability: an unverified technician might have been available.
    /// </summary>
    public string NoCapacityErrorCode()
    {
        var statuses = Rejected.Where(r => r.Reason == FeasibilityReasons.RouteNotVerified && r.RouteStatus is not null).Select(r => r.RouteStatus).ToList();
        if (statuses.Contains(RouteCalculationStatuses.QuotaExceeded)) return SchedulingErrorCodes.TravelProviderQuotaExceeded;
        if (statuses.Any(s => s is RouteCalculationStatuses.ProviderUnavailable or RouteCalculationStatuses.Timeout or RouteCalculationStatuses.RateLimited
                or RouteCalculationStatuses.InvalidResponse or RouteCalculationStatuses.RejectedRequest or RouteCalculationStatuses.ProviderDisabled))
            return SchedulingErrorCodes.TravelProviderUnavailable;
        if (statuses.Contains(RouteCalculationStatuses.RouteNotFound) && statuses.All(s => s == RouteCalculationStatuses.RouteNotFound))
            return SchedulingErrorCodes.TravelRouteUnavailable;
        return SchedulingErrorCodes.SlotUnavailable;
    }

    public bool HadUnverifiedRoutes => Rejected.Any(r => r.Reason == FeasibilityReasons.RouteNotVerified);
}

/// <summary>
/// Finds technicians who can take a candidate appointment.
/// <list type="number">
/// <item>Database-free pre-checks first (base location, daily limit, overlap, unavailable periods).</item>
/// <item>Remaining technicians are ordered by straight-line distance from their previous stop
/// (ordering only — straight-line distance is never used as a travel time).</item>
/// <item>At most <see cref="SchedulingOptions.MaxTechniciansEvaluatedPerSlot"/> are route-checked;
/// the inbound leg is estimated first and the outbound leg only when the inbound already fits.</item>
/// <item>The search stops once <c>maxFeasible</c> technicians are found.</item>
/// </list>
/// </summary>
public sealed class TechnicianAvailabilityEvaluator
{
    private readonly SchedulingOptions _options;
    private readonly TimeProvider _time;

    public TechnicianAvailabilityEvaluator(SchedulingOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    public TimeSpan SafetyBuffer => TimeSpan.FromMinutes(_options.SafetyBufferMinutes);

    public async Task<CandidateEvaluation> EvaluateAsync(
        AppointmentCandidate candidate,
        IReadOnlyList<TechnicianDayState> technicians,
        int maxFeasible,
        RouteBudget budget,
        CancellationToken cancellationToken,
        int? maxEvaluated = null)
    {
        var now = _time.GetUtcNow();
        var rejected = new List<TechnicianRejection>();
        var candidates = new List<(TechnicianDayState Day, LegPlan Plan, double Distance)>();

        foreach (var day in technicians)
        {
            var reason = FeasibilityRules.PreCheck(day, candidate, day.MaxAppointmentsPerDayOverride ?? _options.MaxAppointmentsPerTechnicianPerDay);
            if (reason is not null)
            {
                rejected.Add(new TechnicianRejection(day.TechnicianUserId, reason, null));
                continue;
            }
            var plan = FeasibilityRules.PlanLegs(day, candidate, SafetyBuffer, now);
            candidates.Add((day, plan, plan.Inbound.Origin.StraightLineMetersTo(candidate.Location)));
        }

        var feasible = new List<TechnicianOption>();
        var evaluationLimit = Math.Max(1, maxEvaluated ?? _options.MaxTechniciansEvaluatedPerSlot);
        var evaluated = 0;
        var stoppedEarly = false;
        foreach (var (day, plan, _) in candidates.OrderBy(c => c.Distance).ThenBy(c => c.Day.TechnicianUserId).Take(evaluationLimit))
        {
            evaluated++;
            var inbound = await budget.EstimateAsync(new TravelEstimateRequest(plan.Inbound.Origin, plan.Inbound.Destination, plan.Inbound.DepartureAtUtc), cancellationToken);
            if (!inbound.IsOk)
            {
                rejected.Add(new TechnicianRejection(day.TechnicianUserId, FeasibilityReasons.RouteNotVerified, inbound.Status));
                continue;
            }

            // Check the inbound side alone before spending a request on the outbound leg.
            var inboundOnly = FeasibilityRules.Evaluate(day, candidate, plan with { Next = null, Outbound = null }, inbound, null, SafetyBuffer, now);
            if (!inboundOnly.IsFeasible)
            {
                rejected.Add(new TechnicianRejection(day.TechnicianUserId, inboundOnly.Reason!, null));
                continue;
            }

            TravelEstimateResult? outbound = null;
            if (plan.Outbound is { } leg)
            {
                outbound = await budget.EstimateAsync(new TravelEstimateRequest(leg.Origin, leg.Destination, leg.DepartureAtUtc), cancellationToken);
                if (!outbound.IsOk)
                {
                    rejected.Add(new TechnicianRejection(day.TechnicianUserId, FeasibilityReasons.RouteNotVerified, outbound.Status));
                    continue;
                }
            }

            var result = FeasibilityRules.Evaluate(day, candidate, plan, inbound, outbound, SafetyBuffer, now);
            if (!result.IsFeasible)
            {
                rejected.Add(new TechnicianRejection(day.TechnicianUserId, result.Reason!, null));
                continue;
            }

            feasible.Add(new TechnicianOption(day, plan, inbound, outbound, result));
            if (feasible.Count >= maxFeasible)
            {
                stoppedEarly = evaluated < candidates.Count;
                break;
            }
        }

        var exhaustive = !stoppedEarly
                         && candidates.Count <= evaluationLimit
                         && rejected.All(r => r.Reason != FeasibilityReasons.RouteNotVerified);
        return new CandidateEvaluation(feasible, rejected, exhaustive);
    }
}
