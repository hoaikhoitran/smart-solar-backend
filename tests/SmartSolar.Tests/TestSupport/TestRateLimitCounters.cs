using System.Collections.Concurrent;
using SmartSolar.Modules.Common.RateLimiting;

namespace SmartSolar.Tests.TestSupport;

/// <summary>Stands in for Redis: one instance shared by several API factories behaves like one cluster-wide store.</summary>
public sealed class SharedRateLimitCounter : IDistributedRateLimitCounter
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public int Calls;

    public Task<RateLimitCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)window.TotalMilliseconds;
        var count = _counts.AddOrUpdate($"{key}:{now / windowMs}", 1, (_, c) => c + 1);
        return Task.FromResult(new RateLimitCount(count, TimeSpan.FromMilliseconds(windowMs - now % windowMs)));
    }
}

/// <summary>Redis down: every call fails.</summary>
public sealed class FailingRateLimitCounter : IDistributedRateLimitCounter
{
    public int Calls;

    public Task<RateLimitCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("synthetic Redis outage");
    }
}

/// <summary>Redis unresponsive: calls never complete.</summary>
public sealed class HangingRateLimitCounter : IDistributedRateLimitCounter
{
    public async Task<RateLimitCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return default;
    }
}
