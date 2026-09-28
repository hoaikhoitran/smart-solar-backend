using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartSolar.Infrastructure.Caching;

namespace SmartSolar.Tests;

public sealed class RedisSmokeTests
{
    [Fact]
    public async Task Should_write_and_read_value_from_redis()
    {
        var options = Options.Create(new RedisCacheOptions
        {
            Configuration = "localhost:6379",
            InstanceName = "SmartSolar:"
        });

        using var cache = new RedisCache(options);

        var store = new RedisCacheStore(
            cache,
            NullLogger<RedisCacheStore>.Instance);

        const string key = "redis-smoke-test";

        await store.SetAsync(
            key,
            "hello-smartsolar",
            TimeSpan.FromMinutes(5));

        var value = await store.GetAsync<string>(key);

        Assert.Equal("hello-smartsolar", value);
    }
}