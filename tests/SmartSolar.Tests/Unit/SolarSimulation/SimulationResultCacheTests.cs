using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>The distributed-cache implementation over a fake IDistributedCache (never a real Redis).</summary>
public sealed class SimulationResultCacheTests
{
    private static (DistributedSimulationResultCache Cache, FakeDistributedCache Store, FixedTimeProvider Time, SolarSimulationOptions Options) Create()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = new FakeDistributedCache(time);
        var options = new SolarSimulationOptions();
        return (new DistributedSimulationResultCache(store, options, time, NullLogger<DistributedSimulationResultCache>.Instance), store, time, options);
    }

    /// <summary>The payload is never inspected by the cache, so an uninitialised detail is enough here.</summary>
    private static CachedSimulation Entry(Guid preSurveyId, DateTimeOffset now, bool reusable = true, string fingerprint = "fp", Guid? id = null)
    {
        var detail = (Modules.SolarSimulation.Models.SimulationDetail)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Modules.SolarSimulation.Models.SimulationDetail));
        return new CachedSimulation(id ?? Guid.NewGuid(), preSurveyId, Guid.NewGuid(), Guid.NewGuid(), 1, fingerprint, reusable, now, now.AddMinutes(1440), detail);
    }

    [Fact]
    public async Task Keys_are_versioned_and_the_fingerprint_index_only_covers_reusable_results()
    {
        var (cache, store, time, _) = Create();
        var preSurveyId = Guid.NewGuid();
        var reusable = Entry(preSurveyId, time.Now, fingerprint: "a");
        var failed = Entry(preSurveyId, time.Now, reusable: false, fingerprint: "b");

        await cache.StoreAsync(reusable, CancellationToken.None);
        await cache.StoreAsync(failed, CancellationToken.None);

        Assert.All(store.Keys, k => Assert.StartsWith("solar:simulation:v1:", k));
        Assert.Contains($"solar:simulation:v1:{reusable.SimulationId}", store.Keys);
        Assert.Contains($"solar:simulation:v1:fp:{preSurveyId}:a", store.Keys);
        Assert.DoesNotContain($"solar:simulation:v1:fp:{preSurveyId}:b", store.Keys);
        Assert.Equal(reusable.SimulationId, (await cache.FindByFingerprintAsync(preSurveyId, "a", CancellationToken.None))!.SimulationId);
        Assert.Null(await cache.FindByFingerprintAsync(preSurveyId, "b", CancellationToken.None));
        Assert.Null(await cache.FindByFingerprintAsync(Guid.NewGuid(), "a", CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(failed.SimulationId, CancellationToken.None));
    }

    [Fact]
    public async Task Entries_expire_with_the_clock()
    {
        var (cache, _, time, _) = Create();
        var entry = Entry(Guid.NewGuid(), time.Now);
        await cache.StoreAsync(entry, CancellationToken.None);

        time.Now = entry.ExpiresAt.AddSeconds(-1);
        Assert.NotNull(await cache.GetAsync(entry.SimulationId, CancellationToken.None));
        time.Now = entry.ExpiresAt;
        Assert.Null(await cache.GetAsync(entry.SimulationId, CancellationToken.None));
        Assert.Null(await cache.FindByFingerprintAsync(entry.PreSurveyId, entry.Fingerprint, CancellationToken.None));
    }

    [Fact]
    public async Task Recent_list_is_newest_first_deduplicated_and_bounded()
    {
        var (cache, _, time, options) = Create();
        var preSurveyId = Guid.NewGuid();
        var entries = Enumerable.Range(0, options.MaxRecentResults + 5).Select(i => Entry(preSurveyId, time.Now, fingerprint: $"f{i}")).ToList();
        foreach (var e in entries) await cache.StoreAsync(e, CancellationToken.None);
        await cache.StoreAsync(entries[^1], CancellationToken.None);

        var recent = await cache.GetRecentIdsAsync(preSurveyId, CancellationToken.None);

        Assert.Equal(20, options.MaxRecentResults);
        Assert.Equal(options.MaxRecentResults, recent.Count);
        Assert.Equal(entries[^1].SimulationId, recent[0]);
        Assert.Equal(recent.Count, recent.Distinct().Count());
    }

    [Fact]
    public async Task Outage_throws_unavailable_on_every_operation()
    {
        var (cache, store, time, _) = Create();
        var entry = Entry(Guid.NewGuid(), time.Now);
        store.Outage = true;

        await Assert.ThrowsAsync<SimulationCacheUnavailableException>(() => cache.GetAsync(entry.SimulationId, CancellationToken.None));
        await Assert.ThrowsAsync<SimulationCacheUnavailableException>(() => cache.FindByFingerprintAsync(entry.PreSurveyId, "fp", CancellationToken.None));
        await Assert.ThrowsAsync<SimulationCacheUnavailableException>(() => cache.StoreAsync(entry, CancellationToken.None));
        await Assert.ThrowsAsync<SimulationCacheUnavailableException>(() => cache.GetRecentIdsAsync(entry.PreSurveyId, CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_or_mismatched_entries_behave_like_expired_ones()
    {
        var (cache, store, time, _) = Create();
        var id = Guid.NewGuid();
        store.Put(DistributedSimulationResultCache.ResultKey(id), "{not json");
        Assert.Null(await cache.GetAsync(id, CancellationToken.None));

        // An entry stored under another id's key is never returned for that id.
        var entry = Entry(Guid.NewGuid(), time.Now);
        await cache.StoreAsync(entry, CancellationToken.None);
        store.Put(DistributedSimulationResultCache.ResultKey(id), store.Raw(DistributedSimulationResultCache.ResultKey(entry.SimulationId))!);
        Assert.Null(await cache.GetAsync(id, CancellationToken.None));
    }
}
