namespace SmartSolar.Modules.Files;

/// <summary>An object; with <see cref="VersionId"/>, that exact (immutable) S3 object version.</summary>
public sealed record StorageObjectLocation(string Bucket, string ObjectKey, string? VersionId = null);

public enum StorageProbeStatus
{
    Found = 1,
    NotFound = 2,
    /// <summary>Verification is not configured or the provider could not be reached; the file stays pending.</summary>
    Unavailable = 3,
    TooLarge = 4,
}

/// <summary>What the storage provider reports about an object (never the client's claims).</summary>
public sealed record StorageObjectProbe(
    StorageProbeStatus Status,
    long? SizeBytes,
    string? ContentType,
    string? VersionId,
    string? ETag,
    byte[]? Prefix,
    string? Sha256Hex,
    string? Error)
{
    public static StorageObjectProbe Unavailable(string error) => new(StorageProbeStatus.Unavailable, null, null, null, null, null, null, error);
    public static StorageObjectProbe Missing() => new(StorageProbeStatus.NotFound, null, null, null, null, null, null, "Object not found.");
}

/// <summary>
/// Trusted, read-only verification of frontend-uploaded objects. Implementations never write, never
/// follow client-supplied URLs (only bucket + key against the configured endpoint) and bound every read.
/// </summary>
public interface IStorageObjectVerifier
{
    Task<StorageObjectProbe> ProbeAsync(StorageObjectLocation location, bool computeSha256, long maxBytes, CancellationToken cancellationToken);
}

public interface IFileStore
{
    Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredFile>> FindManyAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string bucket, string objectKey, CancellationToken cancellationToken);
    void Add(StoredFile file);

    /// <summary>Saves; throws <see cref="DuplicateObjectKeyException"/> for an already registered bucket/key and <see cref="FileConcurrencyException"/> for a stale revision.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class DuplicateObjectKeyException(Exception inner) : Exception("The object key is already registered.", inner);

public sealed class FileConcurrencyException(Exception inner) : Exception("The file was changed concurrently.", inner);

/// <summary>A verified file handed to another module (purpose, owner and status already checked).</summary>
public sealed record VerifiedFile(Guid FileId, string Purpose, string Bucket, string ObjectKey, string? VersionId, string ContentType,
    long SizeBytes, string? ChecksumSha256, Guid UploadedByUserId, string OriginalFileName);
