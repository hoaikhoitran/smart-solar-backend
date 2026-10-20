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

    /// <summary>
    /// Existence check only (a single HEAD of the given version; no object bytes are read). Used before issuing a
    /// presigned read URL so a deleted object answers "missing" instead of a URL that fails in the browser.
    /// </summary>
    Task<StorageObjectProbe> HeadAsync(StorageObjectLocation location, CancellationToken cancellationToken);
}

public enum StorageUrlDisposition
{
    /// <summary>Displayed by the browser (an &lt;img&gt; or a new tab).</summary>
    Inline = 1,
    /// <summary>Downloaded under the given file name.</summary>
    Attachment = 2,
}

/// <summary>What the signed GET must make the storage provider answer with (response header overrides) and how long it lives.</summary>
public sealed record StorageUrlRequest(StorageUrlDisposition Disposition, string DownloadFileName, string ContentType, TimeSpan TimeToLive);

public enum StorageUrlStatus
{
    Signed = 1,
    /// <summary>No signer is configured (S3 disabled).</summary>
    NotConfigured = 2,
    /// <summary>The object (or its pinned version) no longer exists in storage.</summary>
    ObjectMissing = 3,
    /// <summary>The provider or the credential could not be reached / used.</summary>
    Unavailable = 4,
}

/// <summary>
/// A short-lived read URL. The URL is a bearer credential for one object: never log it, never persist it,
/// return it only to the caller that was authorized for the object.
/// </summary>
public sealed record StorageUrlResult(StorageUrlStatus Status, string? Url, DateTimeOffset? ExpiresAt)
{
    public static StorageUrlResult NotConfigured() => new(StorageUrlStatus.NotConfigured, null, null);
    public static StorageUrlResult ObjectMissing() => new(StorageUrlStatus.ObjectMissing, null, null);
    public static StorageUrlResult Unavailable() => new(StorageUrlStatus.Unavailable, null, null);
}

/// <summary>
/// Issues presigned GET URLs for objects whose location comes from a stored <see cref="StoredFile"/> (never from a client).
/// Implementations only sign allowed buckets, pin the version when one is given and never log the URL or credentials.
/// Callers go through <see cref="FileReadUrlService"/>, which authorizes the file first.
/// </summary>
public interface IStorageUrlSigner
{
    /// <summary>False when no provider is configured: every request answers <see cref="StorageUrlStatus.NotConfigured"/>.</summary>
    bool IsConfigured { get; }

    Task<StorageUrlResult> CreateReadUrlAsync(StorageObjectLocation location, StorageUrlRequest request, CancellationToken cancellationToken);
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
