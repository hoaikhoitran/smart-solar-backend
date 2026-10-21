using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.Contracts.Caching;

/// <summary>
/// A temporary simulation result as cached. <see cref="Detail"/> is the full payload with neutral
/// view flags (IsStale/IsSelected are computed per read); the other fields let a reader verify
/// pre-survey, owner and geometry version without trusting the payload alone.
/// </summary>
public sealed record CachedSimulation(
    Guid SimulationId,
    Guid PreSurveyId,
    Guid OwnerUserId,
    Guid CreatedByUserId,
    int GeometryVersion,
    string Fingerprint,
    bool IsReusable,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    SimulationDetail Detail);

/// <summary>
/// The cache could not be reached (for example a Redis outage). Surfaced as
/// 503 SIMULATION_CACHE_UNAVAILABLE, never as "expired".
/// </summary>
public sealed class SimulationCacheUnavailableException : Exception
{
    public SimulationCacheUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Temporary storage for full simulation results (distributed cache; Redis, or the in-process
/// distributed memory cache when Redis is disabled). Entries expire after
/// <c>SolarSimulation:ResultTtlMinutes</c>. Every method throws
/// <see cref="SimulationCacheUnavailableException"/> when the cache is unreachable.
/// </summary>
public interface ISimulationResultCache
{
    /// <summary>The cached result, or null when it never existed or has expired.</summary>
    Task<CachedSimulation?> GetAsync(Guid simulationId, CancellationToken cancellationToken);

    /// <summary>A reusable result of this pre-survey with this input fingerprint, still within its TTL.</summary>
    Task<CachedSimulation?> FindByFingerprintAsync(Guid preSurveyId, string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the result, its fingerprint index (reusable results only) and adds it to the
    /// pre-survey's bounded recent list. Idempotent for the same result.
    /// </summary>
    Task StoreAsync(CachedSimulation simulation, CancellationToken cancellationToken);

    /// <summary>Best-effort list of recent result ids of a pre-survey, newest first (entries may have expired).</summary>
    Task<IReadOnlyList<Guid>> GetRecentIdsAsync(Guid preSurveyId, CancellationToken cancellationToken);
}
