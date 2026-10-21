using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.Options;
using StackExchange.Redis;

namespace SmartSolar.Infrastructure.Caching;

/// <summary>
/// Simulation results over <see cref="IDistributedCache"/>: Redis when <c>Redis:Enabled</c>, otherwise
/// the in-process distributed memory cache (single instance only: results are then not shared
/// between replicas and are lost on restart, which is acceptable for temporary results).
/// <list type="bullet">
/// <item><c>solar:simulation:v1:{simulationId}</c> – the full result (JSON), absolute expiry = ExpiresAt.</item>
/// <item><c>solar:simulation:v1:fp:{preSurveyId}:{fingerprint}</c> – simulation id of the reusable result for that input.</item>
/// <item><c>solar:simulation:v1:recent:{preSurveyId}</c> – best-effort JSON list of recent ids (newest first, bounded).
/// It is a read-modify-write without locking, so a concurrent write can drop an id from the list
/// (the result itself stays readable by id).</item>
/// </list>
/// Unlike the catalog cache (which falls back to the database), there is no fallback here: an
/// outage throws <see cref="SimulationCacheUnavailableException"/> so callers can answer 503
/// instead of pretending the result expired. The key prefix is versioned (v1); bump it when the
/// payload shape changes incompatibly. Expiry is also checked against <see cref="TimeProvider"/>.
/// </summary>
public sealed class DistributedSimulationResultCache : ISimulationResultCache
{
    public const string KeyPrefix = "solar:simulation:v1:";

    private readonly IDistributedCache _cache;
    private readonly SolarSimulationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DistributedSimulationResultCache> _logger;

    public DistributedSimulationResultCache(
        IDistributedCache cache,
        SolarSimulationOptions options,
        TimeProvider time,
        ILogger<DistributedSimulationResultCache> logger)
    {
        _cache = cache;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public static string ResultKey(Guid simulationId) => $"{KeyPrefix}{simulationId}";

    public static string FingerprintKey(Guid preSurveyId, string fingerprint) => $"{KeyPrefix}fp:{preSurveyId}:{fingerprint}";

    public static string RecentKey(Guid preSurveyId) => $"{KeyPrefix}recent:{preSurveyId}";

    public async Task<CachedSimulation?> GetAsync(Guid simulationId, CancellationToken cancellationToken)
    {
        var json = await GuardAsync(() => _cache.GetStringAsync(ResultKey(simulationId), cancellationToken));
        if (json is null) return null;

        CachedSimulation? entry;
        try
        {
            entry = SnapshotJson.Deserialize<CachedSimulation>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // An unreadable entry (for example an incompatible payload) behaves like an expired one.
            _logger.LogWarning("Cached simulation {SimulationId} could not be read and was ignored ({ExceptionType}).",
                simulationId, ex.GetType().Name);
            return null;
        }

        return entry.SimulationId == simulationId && entry.ExpiresAt > _time.GetUtcNow() ? entry : null;
    }

    public async Task<CachedSimulation?> FindByFingerprintAsync(Guid preSurveyId, string fingerprint, CancellationToken cancellationToken)
    {
        var value = await GuardAsync(() => _cache.GetStringAsync(FingerprintKey(preSurveyId, fingerprint), cancellationToken));
        if (!Guid.TryParse(value, out var simulationId)) return null;

        var entry = await GetAsync(simulationId, cancellationToken);
        return entry is { IsReusable: true } && entry.PreSurveyId == preSurveyId && entry.Fingerprint == fingerprint ? entry : null;
    }

    public async Task StoreAsync(CachedSimulation simulation, CancellationToken cancellationToken)
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = simulation.ExpiresAt };
        var json = SnapshotJson.Serialize(simulation);

        await GuardAsync(async () =>
        {
            await _cache.SetStringAsync(ResultKey(simulation.SimulationId), json, options, cancellationToken);
            if (simulation.IsReusable)
            {
                await _cache.SetStringAsync(
                    FingerprintKey(simulation.PreSurveyId, simulation.Fingerprint), simulation.SimulationId.ToString(), options, cancellationToken);
            }

            var recent = await ReadRecentAsync(simulation.PreSurveyId, cancellationToken);
            var updated = recent.Where(id => id != simulation.SimulationId).Prepend(simulation.SimulationId)
                .Take(Math.Max(1, _options.MaxRecentResults)).ToList();
            await _cache.SetStringAsync(
                RecentKey(simulation.PreSurveyId), JsonSerializer.Serialize(updated),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.ResultTtlMinutes) },
                cancellationToken);
            return true;
        });
    }

    public async Task<IReadOnlyList<Guid>> GetRecentIdsAsync(Guid preSurveyId, CancellationToken cancellationToken)
        => await GuardAsync(() => ReadRecentAsync(preSurveyId, cancellationToken));

    private async Task<List<Guid>> ReadRecentAsync(Guid preSurveyId, CancellationToken cancellationToken)
    {
        var json = await _cache.GetStringAsync(RecentKey(preSurveyId), cancellationToken);
        if (json is null) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or SocketException)
        {
            _logger.LogWarning("Simulation result cache unavailable ({ExceptionType}).", ex.GetType().Name);
            throw new SimulationCacheUnavailableException("The simulation result cache is unavailable.", ex);
        }
    }
}
