using SmartSolar.Modules.SolarSimulation.CreateSimulation;

namespace SmartSolar.Tests.Unit.SolarSimulation;

public sealed class SimulationSingleFlightTests
{
    private static ComputedSimulation Result() => (ComputedSimulation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ComputedSimulation));

    [Fact]
    public async Task Concurrent_identical_requests_share_one_computation()
    {
        var flight = new SimulationSingleFlight();
        var gate = new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<ComputedSimulation> Compute() { Interlocked.Increment(ref calls); return gate.Task; }

        var a = flight.RunAsync("key", Compute, CancellationToken.None);
        var b = flight.RunAsync("key", Compute, CancellationToken.None);
        var expected = Result();
        gate.SetResult(expected);

        Assert.Same(expected, await a);
        Assert.Same(expected, await b);
        Assert.Equal(1, calls);
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public async Task Completed_entry_is_removed_even_when_every_waiter_cancelled()
    {
        var flight = new SimulationSingleFlight();
        var gate = new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var waiter = flight.RunAsync("key", () => gate.Task, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, flight.InFlightCount);

        gate.SetResult(Result());
        await WaitUntilAsync(() => flight.InFlightCount == 0);

        // A later identical request computes fresh instead of receiving the abandoned result.
        var calls = 0;
        var fresh = Result();
        var later = await flight.RunAsync("key", () => { calls++; return Task.FromResult(fresh); }, CancellationToken.None);
        Assert.Same(fresh, later);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task One_cancelled_waiter_does_not_cancel_the_others()
    {
        var flight = new SimulationSingleFlight();
        var gate = new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var cancelled = flight.RunAsync("key", () => gate.Task, cts.Token);
        var patient = flight.RunAsync("key", () => gate.Task, CancellationToken.None);
        cts.Cancel();
        var expected = Result();
        gate.SetResult(expected);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Same(expected, await patient);
    }

    [Fact]
    public async Task Failed_computation_is_removed_and_a_retry_recomputes()
    {
        var flight = new SimulationSingleFlight();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flight.RunAsync("key", () => Task.FromException<ComputedSimulation>(new InvalidOperationException("synthetic failure")), CancellationToken.None));
        Assert.Equal(0, flight.InFlightCount);

        var expected = Result();
        Assert.Same(expected, await flight.RunAsync("key", () => Task.FromResult(expected), CancellationToken.None));
    }

    [Fact]
    public async Task Synchronously_throwing_factory_is_removed()
    {
        var flight = new SimulationSingleFlight();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            flight.RunAsync("key", () => throw new InvalidOperationException("sync"), CancellationToken.None));

        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public async Task Cleanup_of_an_old_flight_never_removes_a_newer_one()
    {
        var flight = new SimulationSingleFlight();
        var first = new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = flight.RunAsync("key", () => first.Task, CancellationToken.None);
        first.SetResult(Result());
        await firstRun;

        var second = new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = flight.RunAsync("key", () => second.Task, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, flight.InFlightCount);

        second.SetResult(Result());
        await secondRun;
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public async Task Many_distinct_completed_keys_leave_nothing_behind()
    {
        var flight = new SimulationSingleFlight();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var gates = Enumerable.Range(0, 100).Select(_ => new TaskCompletionSource<ComputedSimulation>(TaskCreationOptions.RunContinuationsAsynchronously)).ToList();
        var waiters = gates.Select((g, i) => flight.RunAsync($"key-{i}", () => g.Task, cts.Token)).ToList();
        foreach (var w in waiters) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w);
        foreach (var g in gates) g.SetResult(Result());

        await WaitUntilAsync(() => flight.InFlightCount == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
