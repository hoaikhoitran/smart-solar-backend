using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Files;

public sealed record RegisterFileCommand(
    Guid UploaderUserId, string Purpose, string? Bucket, string ObjectKey, string OriginalFileName,
    string DeclaredContentType, long DeclaredSizeBytes, string? DeclaredChecksumSha256);

public sealed record FileView(
    Guid FileId, string Purpose, string Bucket, string ObjectKey, string OriginalFileName, string Status, string? RejectionReason,
    string? ContentType, long? SizeBytes, string? ChecksumSha256, string? ObjectVersionId, DateTimeOffset? VerifiedAt, DateTimeOffset CreatedAt);

/// <summary>
/// Registers and verifies frontend-uploaded objects. Registration never trusts the client: the
/// key must sit under the purpose prefix and the uploader's own id; verification then asks the
/// storage provider for existence, size, version, real content type (magic bytes) and, where the
/// purpose requires it, a SHA-256 computed by the backend from the object itself. A declared
/// checksum is only compared, never accepted as proof.
/// </summary>
public sealed class FileService(IFileStore store, IStorageObjectVerifier verifier, StorageOptions options, TimeProvider time, ILogger<FileService> logger)
{
    public async Task<OperationResult<FileView>> RegisterAsync(RegisterFileCommand command, CancellationToken cancellationToken)
    {
        if (!FilePolicies.ByPurpose.TryGetValue(command.Purpose?.Trim().ToUpperInvariant() ?? "", out var policy))
            return OperationResult<FileView>.Fail(FileErrorCodes.InvalidPurpose, $"Purpose must be one of {string.Join(", ", FilePurposes.All)}.");

        var bucket = string.IsNullOrWhiteSpace(command.Bucket) ? options.DefaultBucket : command.Bucket.Trim();
        if (string.IsNullOrWhiteSpace(bucket) || !options.Buckets().Contains(bucket))
            return OperationResult<FileView>.Fail(FileErrorCodes.BucketNotAllowed, "The bucket is not configured for this application.");

        var keyError = FilePolicies.ValidateKey(policy, command.ObjectKey ?? "", command.UploaderUserId);
        if (keyError is not null) return OperationResult<FileView>.Fail(FileErrorCodes.InvalidObjectKey, keyError);

        var declared = command.DeclaredContentType?.Trim().ToLowerInvariant() ?? "";
        if (!policy.AllowedContentTypes.Contains(declared) || !FilePolicies.ExtensionMatches(declared, command.OriginalFileName ?? "")
            || !FilePolicies.ExtensionMatches(declared, command.ObjectKey!))
            return OperationResult<FileView>.Fail(FileErrorCodes.TypeNotAllowed,
                $"Allowed types for {policy.Purpose}: {string.Join(", ", policy.AllowedContentTypes)} with a matching file extension.");
        if (command.DeclaredSizeBytes <= 0 || command.DeclaredSizeBytes > policy.MaxSizeBytes)
            return OperationResult<FileView>.Fail(FileErrorCodes.TooLarge, $"Files for {policy.Purpose} must be between 1 byte and {policy.MaxSizeBytes} bytes.");

        if (await store.ExistsAsync(bucket, command.ObjectKey!, cancellationToken))
            return OperationResult<FileView>.Fail(FileErrorCodes.AlreadyRegistered, "This object is already registered; upload new content under a new key.");

        var now = time.GetUtcNow();
        var file = new StoredFile
        {
            Id = Guid.NewGuid(), Purpose = policy.Purpose, Provider = options.Provider, Bucket = bucket, ObjectKey = command.ObjectKey!,
            OriginalFileName = Path.GetFileName(command.OriginalFileName!.Trim()), DeclaredContentType = declared,
            DeclaredSizeBytes = command.DeclaredSizeBytes, DeclaredChecksumSha256 = NormalizeChecksum(command.DeclaredChecksumSha256),
            Status = FileStatuses.PendingVerification, UploadedByUserId = command.UploaderUserId, CreatedAt = now, UpdatedAt = now,
        };
        store.Add(file);
        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateObjectKeyException)
        {
            return OperationResult<FileView>.Fail(FileErrorCodes.AlreadyRegistered, "This object is already registered; upload new content under a new key.");
        }

        return await VerifyAsync(file.Id, command.UploaderUserId, isStaff: false, cancellationToken);
    }

    /// <summary>(Re)verifies a pending file. Verified and rejected files are returned unchanged (idempotent).</summary>
    public async Task<OperationResult<FileView>> VerifyAsync(Guid fileId, Guid actorUserId, bool isStaff, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken);
        if (file is null || (!isStaff && file.UploadedByUserId != actorUserId))
            return OperationResult<FileView>.Fail(FileErrorCodes.NotFound, "File was not found.");
        if (file.Status != FileStatuses.PendingVerification) return OperationResult<FileView>.Ok(View(file));

        var policy = FilePolicies.ByPurpose[file.Purpose];
        // Provider call with no transaction open; the result is written with an optimistic revision check.
        var probe = await verifier.ProbeAsync(new StorageObjectLocation(file.Bucket, file.ObjectKey), policy.RequireVerifiedChecksum, policy.MaxSizeBytes, cancellationToken);
        Apply(file, probe, policy, time.GetUtcNow(), options.S3.RequireObjectVersioning);
        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (FileConcurrencyException)
        {
            // Someone else verified it first: return their result.
            file = await store.FindAsync(fileId, cancellationToken);
        }

        if (file!.Status == FileStatuses.Rejected)
            logger.LogWarning("File {FileId} rejected during verification: {Reason}.", file.Id, file.RejectionReason);
        return OperationResult<FileView>.Ok(View(file));
    }

    public async Task<OperationResult<FileView>> GetAsync(Guid fileId, Guid actorUserId, bool isStaff, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken);
        return file is null || (!isStaff && file.UploadedByUserId != actorUserId)
            ? OperationResult<FileView>.Fail(FileErrorCodes.NotFound, "File was not found.")
            : OperationResult<FileView>.Ok(View(file));
    }

    /// <summary>
    /// Used by other modules before linking files: every id must exist, be VERIFIED, have the expected
    /// purpose and (when given) have been registered by <paramref name="requiredUploaderUserId"/>.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<VerifiedFile>>> RequireVerifiedAsync(
        IReadOnlyCollection<Guid> fileIds, string purpose, Guid? requiredUploaderUserId, CancellationToken cancellationToken)
    {
        var distinct = fileIds.Distinct().ToList();
        var files = await store.FindManyAsync(distinct, cancellationToken);
        foreach (var id in distinct)
        {
            var file = files.FirstOrDefault(f => f.Id == id);
            if (file is null) return OperationResult<IReadOnlyList<VerifiedFile>>.Fail(FileErrorCodes.NotFound, $"File {id} was not found.");
            if (file.Purpose != purpose) return OperationResult<IReadOnlyList<VerifiedFile>>.Fail(FileErrorCodes.WrongPurpose, $"File {id} was not registered for {purpose}.");
            if (requiredUploaderUserId is { } owner && file.UploadedByUserId != owner)
                return OperationResult<IReadOnlyList<VerifiedFile>>.Fail(FileErrorCodes.NotOwned, $"File {id} was registered by another user.");
            if (file.Status != FileStatuses.Verified)
                return OperationResult<IReadOnlyList<VerifiedFile>>.Fail(FileErrorCodes.NotVerified,
                    $"File {id} is {file.Status}; only files verified against storage can be used.");
        }

        return OperationResult<IReadOnlyList<VerifiedFile>>.Ok(files.Select(ToVerified).ToList());
    }

    /// <summary>
    /// Re-reads the pinned object version of a verified file and compares size and SHA-256 with what was
    /// verified. A changed or missing object rejects the file (it can no longer be used anywhere); an
    /// unreachable store fails closed without touching the file. Call it, with no transaction open,
    /// before decisions that rely on the document (for example approving a signed contract).
    /// </summary>
    public async Task<OperationResult<bool>> VerifyIntegrityAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken);
        if (file is null) return OperationResult<bool>.Fail(FileErrorCodes.NotFound, $"File {fileId} was not found.");
        if (file.Status != FileStatuses.Verified || file.VerifiedChecksumSha256 is null)
            return OperationResult<bool>.Fail(FileErrorCodes.NotVerified, $"File {fileId} is not verified.");

        var policy = FilePolicies.ByPurpose[file.Purpose];
        var probe = await verifier.ProbeAsync(new StorageObjectLocation(file.Bucket, file.ObjectKey, file.ObjectVersionId), computeSha256: true, policy.MaxSizeBytes, cancellationToken);
        if (probe.Status == StorageProbeStatus.Unavailable)
            return OperationResult<bool>.Fail(FileErrorCodes.StorageUnavailable, "File storage could not be reached; try again later.");

        var intact = probe.Status == StorageProbeStatus.Found && probe.SizeBytes == file.VerifiedSizeBytes
                     && string.Equals(probe.Sha256Hex, file.VerifiedChecksumSha256, StringComparison.OrdinalIgnoreCase)
                     && (file.ObjectVersionId is null || probe.VersionId == file.ObjectVersionId);
        if (intact) return OperationResult<bool>.Ok(true);

        var reason = probe.Status == StorageProbeStatus.Found ? "The stored object changed after verification." : "The verified object no longer exists in storage.";
        Reject(file, reason);
        file.UpdatedAt = time.GetUtcNow();
        file.Revision++;
        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (FileConcurrencyException)
        {
            // Concurrent re-check: the outcome is the same.
        }
        logger.LogWarning("File {FileId} failed its integrity check: {Reason}", file.Id, reason);
        return OperationResult<bool>.Fail(FileErrorCodes.ContentChanged, $"File {fileId}: {reason}");
    }

    internal static void Apply(StoredFile file, StorageObjectProbe probe, FilePurposePolicy policy, DateTimeOffset now, bool requireVersion = true)
    {
        file.VerificationAttempts++;
        file.UpdatedAt = now;
        file.Revision++;
        switch (probe.Status)
        {
            case StorageProbeStatus.Unavailable:
                // Stay pending: never treat an unverifiable object as valid.
                file.RejectionReason = null;
                return;
            case StorageProbeStatus.NotFound:
                Reject(file, "The object does not exist in storage.");
                return;
            case StorageProbeStatus.TooLarge:
                Reject(file, $"The object is larger than {policy.MaxSizeBytes} bytes.");
                return;
        }

        var detected = FilePolicies.DetectContentType(probe.Prefix ?? [], file.DeclaredContentType);
        if (probe.SizeBytes is not { } size || size <= 0 || size > policy.MaxSizeBytes) { Reject(file, "The stored object size is outside the allowed range."); return; }
        if (size != file.DeclaredSizeBytes) { Reject(file, "The stored object size does not match the declared size."); return; }
        if (detected is null || !policy.AllowedContentTypes.Contains(detected) || detected != file.DeclaredContentType)
        { Reject(file, "The stored content does not match an allowed file type."); return; }
        if (policy.RequireVerifiedChecksum && probe.Sha256Hex is null) { Reject(file, "A checksum could not be computed from the stored object."); return; }
        if (requireVersion && string.IsNullOrEmpty(probe.VersionId))
        { Reject(file, "The bucket must have versioning enabled so the verified content cannot be replaced."); return; }
        if (file.DeclaredChecksumSha256 is { } declared && probe.Sha256Hex is { } actual && !string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase))
        { Reject(file, "The stored object checksum does not match the declared checksum."); return; }

        file.Status = FileStatuses.Verified;
        file.VerifiedContentType = detected;
        file.VerifiedSizeBytes = size;
        file.VerifiedChecksumSha256 = probe.Sha256Hex?.ToLowerInvariant();
        file.ObjectVersionId = probe.VersionId;
        file.ETag = probe.ETag;
        file.VerifiedAt = now;
        file.RejectionReason = null;
    }

    private static void Reject(StoredFile file, string reason)
    {
        file.Status = FileStatuses.Rejected;
        file.RejectionReason = reason;
    }

    private static string? NormalizeChecksum(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant() is { Length: 64 } hex && hex.All(Uri.IsHexDigit) ? hex : null;

    public static FileView View(StoredFile f) => new(f.Id, f.Purpose, f.Bucket, f.ObjectKey, f.OriginalFileName, f.Status, f.RejectionReason,
        f.VerifiedContentType, f.VerifiedSizeBytes, f.VerifiedChecksumSha256, f.ObjectVersionId, f.VerifiedAt, f.CreatedAt);

    public static VerifiedFile ToVerified(StoredFile f) => new(f.Id, f.Purpose, f.Bucket, f.ObjectKey, f.ObjectVersionId, f.VerifiedContentType!,
        f.VerifiedSizeBytes!.Value, f.VerifiedChecksumSha256, f.UploadedByUserId, f.OriginalFileName);
}
