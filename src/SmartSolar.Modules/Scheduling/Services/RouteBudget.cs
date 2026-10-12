using System.Collections.Concurrent;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// Per-operation guard for travel estimates: identical legs are requested once, and at most
/// <c>limit</c> distinct requests are made. Beyond the limit legs come back as
/// <see cref="RouteCalculationStatuses.BudgetExhausted"/> (never as an invented duration).
/// </summary>
public sealed class RouteBudget
{
    private readonly ITravelTimeEstimator _estimator;
    private readonly ConcurrentDictionary<string, Lazy<Task<TravelEstimateResult>>> _legs = new();
    private int _remaining;
    private int _made;

    public RouteBudget(ITravelTimeEstimator estimator, int limit)
    {
        _estimator = estimator;
        _remaining = Math.Max(0, limit);
    }

    public int RequestsMade => Volatile.Read(ref _made);

    public Task<TravelEstimateResult> EstimateAsync(TravelEstimateRequest request, CancellationToken cancellationToken)
    {
        var key = $"{request.Origin.Latitude}|{request.Origin.Longitude}|{request.Destination.Latitude}|{request.Destination.Longitude}|{request.DepartureAtUtc.UtcTicks}";
        return _legs.GetOrAdd(key, _ => new Lazy<Task<TravelEstimateResult>>(() =>
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
            {
                return Task.FromResult(new TravelEstimateResult(RouteCalculationStatuses.BudgetExhausted, "NONE", "-", "motorcycle",
                    request.DepartureAtUtc, DateTimeOffset.UtcNow, null, null, null, "Routing request limit for this operation reached."));
            }
            Interlocked.Increment(ref _made);
            return _estimator.EstimateAsync(request, cancellationToken);
        })).Value;
    }
}
