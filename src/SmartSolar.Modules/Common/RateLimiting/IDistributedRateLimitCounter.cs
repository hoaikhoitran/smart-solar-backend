namespace SmartSolar.Modules.Common.RateLimiting;

/// <summary>Result of one atomic increment of a shared fixed-window counter.</summary>
public readonly record struct RateLimitCount(long Count, TimeSpan ResetIn);

/// <summary>
/// Shared (cluster-wide) fixed-window counter used by the distributed rate-limit policies. Implemented
/// with Redis; when it fails, callers fall back to a per-instance limiter.
/// </summary>
public interface IDistributedRateLimitCounter
{
    /// <summary>Atomically counts one request for <paramref name="key"/> in the current window of <paramref name="window"/>.</summary>
    Task<RateLimitCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken);
}
