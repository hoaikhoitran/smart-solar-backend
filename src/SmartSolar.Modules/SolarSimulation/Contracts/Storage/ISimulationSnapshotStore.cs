using System.Security.Cryptography;

namespace SmartSolar.Modules.SolarSimulation.Contracts.Storage;

/// <summary>Where a durable snapshot lives and what its bytes must hash to.</summary>
public sealed record SimulationSnapshotLocation(string Bucket, string ObjectKey, string Sha256, long SizeBytes);

public enum SnapshotReadStatus
{
    Found,
    Missing,
    Unavailable
}

public sealed record SnapshotReadResult(SnapshotReadStatus Status, byte[]? Content)
{
    public static SnapshotReadResult Missing() => new(SnapshotReadStatus.Missing, null);

    public static SnapshotReadResult Unavailable() => new(SnapshotReadStatus.Unavailable, null);
}

/// <summary>The object store could not be written; the selection proceeds without a snapshot.</summary>
public sealed class SimulationSnapshotUnavailableException : Exception
{
    public SimulationSnapshotUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Optional durable copy of a selected simulation's full payload
/// (<c>SolarSimulation:Snapshots:Enabled</c>, default false). The S3 implementation writes with
/// a SHA-256 checksum; readers always re-verify the bytes against the hash recorded in PostgreSQL.
/// </summary>
public interface ISimulationSnapshotStore
{
    bool IsEnabled { get; }

    /// <summary>Uploads the snapshot; throws <see cref="SimulationSnapshotUnavailableException"/> on failure.</summary>
    Task<SimulationSnapshotLocation> PutAsync(Guid preSurveyId, Guid simulationId, byte[] content, CancellationToken cancellationToken);

    /// <summary>Raw bytes as stored. Integrity is verified by the caller with <see cref="SnapshotIntegrity"/>.</summary>
    Task<SnapshotReadResult> GetAsync(SimulationSnapshotLocation location, CancellationToken cancellationToken);
}

public static class SimulationSnapshotKeys
{
    public static string ObjectKey(Guid preSurveyId, Guid simulationId) => $"simulations/{preSurveyId}/{simulationId}.json";
}

public static class SnapshotIntegrity
{
    public static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>True only when size and SHA-256 both match what was recorded at selection time.</summary>
    public static bool Matches(byte[] content, SimulationSnapshotLocation location)
        => content.LongLength == location.SizeBytes
           && string.Equals(Sha256Hex(content), location.Sha256, StringComparison.OrdinalIgnoreCase);
}
