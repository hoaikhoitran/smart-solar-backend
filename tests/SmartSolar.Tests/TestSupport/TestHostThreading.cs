using System.Runtime.CompilerServices;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Test-host only (never the API): xUnit runs ~one test class per core in parallel and most integration tests host the full API on
/// SQLite, whose EF Core "async" calls complete synchronously and hold thread-pool threads. With the default minimum the pool grows by
/// about one thread per 0.5 s, so under a full run single in-process HTTP requests waited 2.5–10 s (measured on the unchanged HEAD:
/// rate-limit windows rolled over mid-test and a request hit the 100 s HttpClient timeout). Starting with enough threads removes that
/// artificial queueing; production uses Npgsql (truly asynchronous) and is not affected.
/// </summary>
internal static class TestHostThreading
{
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var target = Math.Max(64, Environment.ProcessorCount * 8);
        ThreadPool.SetMinThreads(Math.Max(worker, target), Math.Max(io, target));
    }
}

/// <summary>
/// Tests whose assertions depend on the real wall clock (fixed rate-limit windows, request latency measurements, the 100 s HttpClient
/// timeout around in-process simulations that ray-cast shading): they run alone, after the parallel collections, so another class's CPU or
/// database load cannot move their time windows. Measured on the unchanged HEAD and on this branch: the same simulation request takes ~2 s
/// alone and exceeded 100 s during a full parallel run; CreateSimulationHandlerTests itself holds a core for ~35 s (rack ray casting).
/// Nothing is skipped or relaxed.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockSensitiveCollection
{
    public const string Name = "Wall-clock sensitive (not parallel)";
}
