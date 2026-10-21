using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using StackExchange.Redis;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IDistributedCache"/> for simulation-cache tests: expiry follows the given
/// (controllable) clock, and <see cref="Outage"/> makes every call throw a Redis connection error,
/// exactly as StackExchange.Redis does when the server is unreachable. Never talks to a real Redis.
/// </summary>
public sealed class FakeDistributedCache(TimeProvider time) : IDistributedCache
{
    private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset? ExpiresAt)> _entries = new();

    public bool Outage { get; set; }

    public IReadOnlyCollection<string> Keys => _entries.Keys.ToList();

    public string? Raw(string key) => _entries.TryGetValue(key, out var e) ? System.Text.Encoding.UTF8.GetString(e.Value) : null;

    public void Put(string key, string value) => _entries[key] = (System.Text.Encoding.UTF8.GetBytes(value), null);

    public byte[]? Get(string key)
    {
        ThrowIfDown();
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAt is { } expires && expires <= time.GetUtcNow())
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry.Value;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ThrowIfDown();
        DateTimeOffset? expires = options.AbsoluteExpiration
            ?? (options.AbsoluteExpirationRelativeToNow is { } relative ? time.GetUtcNow() + relative : null);
        _entries[key] = (value, expires);
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) => ThrowIfDown();

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        Refresh(key);
        return Task.CompletedTask;
    }

    public void Remove(string key)
    {
        ThrowIfDown();
        _entries.TryRemove(key, out _);
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    private void ThrowIfDown()
    {
        if (Outage) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic Redis outage.");
    }
}

/// <summary>In-memory snapshot store (never S3). Objects can be tampered with to test integrity checks.</summary>
public sealed class FakeSimulationSnapshotStore : ISimulationSnapshotStore
{
    public const string Bucket = "synthetic-snapshots";

    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public bool IsEnabled { get; set; }

    public bool FailWrites { get; set; }

    public bool Unreachable { get; set; }

    public int Puts;

    public Task<SimulationSnapshotLocation> PutAsync(Guid preSurveyId, Guid simulationId, byte[] content, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Puts);
        if (FailWrites) throw new SimulationSnapshotUnavailableException("Synthetic snapshot store outage.");
        var key = SimulationSnapshotKeys.ObjectKey(preSurveyId, simulationId);
        Objects[key] = content.ToArray();
        return Task.FromResult(new SimulationSnapshotLocation(Bucket, key, SnapshotIntegrity.Sha256Hex(content), content.LongLength));
    }

    public Task<SnapshotReadResult> GetAsync(SimulationSnapshotLocation location, CancellationToken cancellationToken)
    {
        if (Unreachable) return Task.FromResult(SnapshotReadResult.Unavailable());
        return Task.FromResult(Objects.TryGetValue(location.ObjectKey, out var bytes)
            ? new SnapshotReadResult(SnapshotReadStatus.Found, bytes.ToArray())
            : SnapshotReadResult.Missing());
    }

    /// <summary>Flips one bit of the stored object (same size, different SHA-256).</summary>
    public void Tamper(string objectKey)
    {
        var bytes = Objects[objectKey].ToArray();
        bytes[bytes.Length / 2] ^= 0x01;
        Objects[objectKey] = bytes;
    }
}
