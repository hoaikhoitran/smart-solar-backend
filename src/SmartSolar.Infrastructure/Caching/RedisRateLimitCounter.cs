using SmartSolar.Modules.Common.RateLimiting;
using StackExchange.Redis;

namespace SmartSolar.Infrastructure.Caching;

/// <summary>
/// Fixed-window counter in Redis. One Lua script does INCR + PEXPIRE atomically and derives the window
/// from Redis' own clock (TIME), so every API replica counts into the same window regardless of local
/// clock skew. Keys expire with their window, so nothing accumulates. Standalone/replicated Redis
/// (the key is derived inside the script, so Redis Cluster is not supported).
/// </summary>
public sealed class RedisRateLimitCounter(IConnectionMultiplexer redis, string keyPrefix) : IDistributedRateLimitCounter
{
    private const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[1])
        local key = KEYS[1] .. ':' .. math.floor(now / window)
        local count = redis.call('INCR', key)
        if count == 1 then redis.call('PEXPIRE', key, window) end
        return { count, window - (now % window) }
        """;

    public async Task<RateLimitCount> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windowMs = (long)Math.Max(1, window.TotalMilliseconds);
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(Script, [new RedisKey(keyPrefix + key)], [windowMs]))!;
        return new RateLimitCount((long)result[0], TimeSpan.FromMilliseconds((long)result[1]));
    }
}
