using System.Collections.Concurrent;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// Collapses identical in-flight calculations on one instance into a single computation, so
/// simultaneous identical requests call PVGIS and NASA POWER once. Across instances the
/// unique fingerprint index decides the winner.
/// <para>
/// Each registration removes itself when its computation finishes (success, failure or a
/// synchronous throw), independently of who is still waiting, and removes only its own entry,
/// never a newer registration for the same key. A waiter's cancellation only stops that waiter;
/// the shared computation keeps its own time budget. Nothing is retained after completion, so
/// a retry after a failure always computes again.
/// </para>
/// </summary>
public sealed class SimulationSingleFlight
{
    private readonly ConcurrentDictionary<string, Flight> _inFlight = new();

    /// <summary>Number of calculations currently registered (for monitoring and tests).</summary>
    public int InFlightCount => _inFlight.Count;

    public Task<ComputedSimulation> RunAsync(string key, Func<Task<ComputedSimulation>> compute, CancellationToken cancellationToken)
    {
        var candidate = new Flight();
        // Work is assigned before the entry is published, so every waiter sees it.
        candidate.Work = new Lazy<Task<ComputedSimulation>>(
            () => ExecuteAsync(key, candidate, compute),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var flight = _inFlight.GetOrAdd(key, candidate);
        return flight.Work.Value.WaitAsync(cancellationToken);
    }

    private async Task<ComputedSimulation> ExecuteAsync(string key, Flight flight, Func<Task<ComputedSimulation>> compute)
    {
        try
        {
            // Yield first so the registration is complete before the computation (and its
            // cleanup) can run, even when the factory finishes or throws synchronously.
            await Task.Yield();
            return await compute();
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
        }
    }

    private sealed class Flight
    {
        public Lazy<Task<ComputedSimulation>> Work { get; set; } = null!;
    }
}
