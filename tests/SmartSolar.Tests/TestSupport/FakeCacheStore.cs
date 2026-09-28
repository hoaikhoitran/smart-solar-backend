using System.Text.Json;
using SmartSolar.Modules.Common.Caching;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// In-memory ICacheStore that round-trips values through JSON like the Redis
/// store does, and records every call. Can be told to fail like an outage.
/// </summary>
public sealed class FakeCacheStore : ICacheStore
{
    private readonly Dictionary<string, (string Json, TimeSpan Expiration)> _entries = new();

    public bool Fail { get; set; }

    public List<string> Gets { get; } = new();

    public List<string> Sets { get; } = new();

    public List<string> Removes { get; } = new();

    public bool Contains(string key) => _entries.ContainsKey(key);

    public TimeSpan ExpirationOf(string key) => _entries[key].Expiration;

    public void Seed<T>(string key, T value) => _entries[key] = (JsonSerializer.Serialize(value), TimeSpan.FromMinutes(10));

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        Gets.Add(key);
        ThrowIfFailing();

        return Task.FromResult(_entries.TryGetValue(key, out var entry)
            ? JsonSerializer.Deserialize<T>(entry.Json)
            : default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        Sets.Add(key);
        ThrowIfFailing();

        _entries[key] = (JsonSerializer.Serialize(value), expiration);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Removes.Add(key);
        ThrowIfFailing();

        _entries.Remove(key);
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Fail)
        {
            // Not a RedisException: the kind of failure the store does not swallow.
            throw new TimeoutException("Simulated cache outage.");
        }
    }
}
