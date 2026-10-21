using System.Collections.Concurrent;
using System.Security.Cryptography;
using SmartSolar.Modules.Files;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SYNTHETIC object store for tests: objects are "uploaded" with <see cref="Put"/> and the verifier
/// answers from them exactly like a provider would (size, real bytes, computed SHA-256). Never calls S3.
/// With <see cref="Versioned"/> (the default, like an S3 bucket with versioning enabled) every upload to
/// a key creates a new immutable version; without it an upload silently replaces the bytes.
/// </summary>
public sealed class FakeStorageObjectVerifier : IStorageObjectVerifier
{
    private readonly ConcurrentDictionary<string, List<(string VersionId, byte[] Bytes)>> _objects = new();
    private int _versions;

    public bool Unavailable { get; set; }
    public bool Versioned { get; set; } = true;
    public int Calls;

    public static readonly byte[] PdfBytes = "%PDF-1.7\n% synthetic test document\n"u8.ToArray();
    public static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03];
    public static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    /// <summary>Uploads <paramref name="bytes"/>; returns the new version id (null for an unversioned bucket).</summary>
    public string? Put(string bucket, string key, byte[] bytes)
    {
        var versions = _objects.GetOrAdd($"{bucket}/{key}", _ => []);
        lock (versions)
        {
            if (!Versioned)
            {
                versions.Clear();
                versions.Add(("null", bytes));
                return null;
            }
            var id = $"v{Interlocked.Increment(ref _versions)}";
            versions.Add((id, bytes));
            return id;
        }
    }

    public void Delete(string bucket, string key) => _objects.TryRemove($"{bucket}/{key}", out _);

    public void DeleteVersion(string bucket, string key, string versionId)
    {
        if (!_objects.TryGetValue($"{bucket}/{key}", out var versions)) return;
        lock (versions) versions.RemoveAll(v => v.VersionId == versionId);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Calls of <see cref="HeadAsync"/> (existence checks before signing a read URL).</summary>
    public int HeadCalls;

    public Task<StorageObjectProbe> HeadAsync(StorageObjectLocation location, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref HeadCalls);
        if (Unavailable) return Task.FromResult(StorageObjectProbe.Unavailable("synthetic outage"));
        if (!_objects.TryGetValue($"{location.Bucket}/{location.ObjectKey}", out var versions)) return Task.FromResult(StorageObjectProbe.Missing());
        (string VersionId, byte[] Bytes)? match;
        lock (versions)
            match = location.VersionId is { } wanted ? versions.Where(v => v.VersionId == wanted).Cast<(string, byte[])?>().FirstOrDefault()
                : versions.Count > 0 ? versions[^1] : null;
        return Task.FromResult(match is { } found
            ? new StorageObjectProbe(StorageProbeStatus.Found, found.Bytes.Length, "application/octet-stream", found.VersionId == "null" ? null : found.VersionId, "\"etag\"", null, null, null)
            : StorageObjectProbe.Missing());
    }

    public Task<StorageObjectProbe> ProbeAsync(StorageObjectLocation location, bool computeSha256, long maxBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Unavailable) return Task.FromResult(StorageObjectProbe.Unavailable("synthetic outage"));
        if (!_objects.TryGetValue($"{location.Bucket}/{location.ObjectKey}", out var versions)) return Task.FromResult(StorageObjectProbe.Missing());
        (string VersionId, byte[] Bytes)? match;
        lock (versions)
            match = location.VersionId is { } wanted ? versions.Where(v => v.VersionId == wanted).Cast<(string, byte[])?>().FirstOrDefault()
                : versions.Count > 0 ? versions[^1] : null;
        if (match is not { } found) return Task.FromResult(StorageObjectProbe.Missing());
        var (versionId, bytes) = found;
        var reportedVersion = versionId == "null" ? null : versionId;
        if (bytes.Length > maxBytes) return Task.FromResult(new StorageObjectProbe(StorageProbeStatus.TooLarge, bytes.Length, null, reportedVersion, null, null, null, null));
        return Task.FromResult(new StorageObjectProbe(StorageProbeStatus.Found, bytes.Length, "application/octet-stream", reportedVersion, "\"etag\"",
            bytes.Take(16).ToArray(), computeSha256 ? Sha256(bytes) : null, null));
    }
}
