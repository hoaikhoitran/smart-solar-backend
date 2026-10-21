using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Files;

public sealed record CreateUploadUrlCommand(Guid UserId, Func<string, bool> IsInRole, string Purpose, string FileName, string ContentType, long SizeBytes);

/// <summary>
/// Where and how the frontend uploads one file. <see cref="Headers"/> must be sent exactly as given (Content-Type is signed).
/// After the upload succeeds the frontend registers <see cref="ObjectKey"/> with POST /api/files.
/// </summary>
public sealed record UploadUrlView(string UploadUrl, string Method, string Bucket, string ObjectKey, IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Step 1 of an upload: checks the caller may upload this purpose and that the declared file fits the purpose policy, then
/// returns a short-lived presigned PUT URL for a NEW key "{purpose prefix}{userId}/{random id}{extension}". The key is always
/// generated here (never taken from the client), so it passes the ownership check of <see cref="FileService.RegisterAsync"/>.
/// Nothing is stored: the file only exists for the backend once it is registered and verified. Logs never contain the URL.
/// </summary>
public sealed class FileUploadUrlService(IStorageUploadUrlSigner signer, StorageOptions options, TimeProvider time, ILogger<FileUploadUrlService> logger)
{
    public async Task<OperationResult<UploadUrlView>> CreateAsync(CreateUploadUrlCommand command, CancellationToken cancellationToken)
    {
        var purpose = command.Purpose?.Trim().ToUpperInvariant() ?? "";
        if (!FilePolicies.ByPurpose.TryGetValue(purpose, out var policy))
            return Fail(FileErrorCodes.InvalidPurpose, $"Purpose must be one of {string.Join(", ", FilePurposes.All)}.");
        if (!FilePolicies.UploaderRoles[purpose].Any(command.IsInRole))
            return Fail(FileErrorCodes.UploadNotAllowed, $"Your role cannot upload {purpose} files.");

        var fileName = Path.GetFileName(command.FileName?.Trim() ?? "");
        var contentType = command.ContentType?.Trim().ToLowerInvariant() ?? "";
        if (FilePolicies.CheckTypeAndSize(policy, contentType, fileName, command.SizeBytes) is { } invalid)
            return Fail(invalid.Code, invalid.Message);

        var bucket = UploadBucket();
        if (!signer.IsConfigured || bucket is null)
            return Fail(StorageErrorCodes.NotConfigured, "File storage is not configured.");

        var objectKey = NewObjectKey(policy, command.UserId, contentType);
        var timeToLive = TimeSpan.FromSeconds(Math.Clamp(options.S3.PresignedUrlTtlSeconds,
            S3VerificationOptions.MinPresignedUrlTtlSeconds, S3VerificationOptions.MaxPresignedUrlTtlSeconds));
        var signed = await signer.CreateUploadUrlAsync(new StorageObjectLocation(bucket, objectKey), contentType, timeToLive, cancellationToken);
        if (signed.Status != StorageUrlStatus.Signed || signed.Url is null)
        {
            logger.LogWarning("Upload URL not issued for user {UserId} ({Purpose}): {Outcome}.", command.UserId, purpose, signed.Status);
            return signed.Status == StorageUrlStatus.NotConfigured
                ? Fail(StorageErrorCodes.NotConfigured, "File storage is not configured.")
                : Fail(StorageErrorCodes.Unavailable, "The upload URL could not be created; try again later.");
        }

        logger.LogInformation("Upload URL issued for user {UserId} ({Purpose}).", command.UserId, purpose);
        var headers = new Dictionary<string, string> { ["Content-Type"] = contentType };
        return OperationResult<UploadUrlView>.Ok(new UploadUrlView(signed.Url, "PUT", bucket, objectKey, headers,
            signed.ExpiresAt ?? time.GetUtcNow().Add(timeToLive)));
    }

    /// <summary>The same key format RegisterAsync expects: purpose prefix, the uploader's id, then a random name with the type's extension.</summary>
    public static string NewObjectKey(FilePurposePolicy policy, Guid userId, string contentType)
        => $"{policy.KeyPrefix}{userId:D}/{Guid.NewGuid():N}{FilePolicies.CanonicalExtension(contentType)}";

    private string? UploadBucket()
        => !string.IsNullOrWhiteSpace(options.DefaultBucket) ? options.DefaultBucket.Trim() : options.Buckets().FirstOrDefault();

    private static OperationResult<UploadUrlView> Fail(string code, string message) => OperationResult<UploadUrlView>.Fail(code, message);
}
