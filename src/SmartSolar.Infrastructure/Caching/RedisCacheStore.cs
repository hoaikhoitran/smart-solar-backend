using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Caching;
using StackExchange.Redis;

namespace SmartSolar.Infrastructure.Caching;

public sealed class RedisCacheStore : ICacheStore
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheStore> _logger;

    public RedisCacheStore(
        IDistributedCache cache,
        ILogger<RedisCacheStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _cache.GetStringAsync(
                key,
                cancellationToken);

            if (value is null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(value);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(
                ex,
                "Redis GET failed for key {CacheKey}. Falling back to source.",
                key);

            return default;
        }
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(value);

            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            };

            await _cache.SetStringAsync(
                key,
                json,
                options,
                cancellationToken);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(
                ex,
                "Redis SET failed for key {CacheKey}.",
                key);
        }
    }

    public async Task RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(
                key,
                cancellationToken);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(
                ex,
                "Redis REMOVE failed for key {CacheKey}.",
                key);
        }
    }
}