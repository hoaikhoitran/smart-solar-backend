using System.Text;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Files;

/// <summary>A presigned read URL handed to an authorized caller. No bucket, key, version id or credential is exposed besides the URL itself.</summary>
public sealed record FileReadUrl(string Url, DateTimeOffset ExpiresAt, StorageUrlDisposition Disposition, string ContentType, string FileName);

/// <summary>
/// Issues short-lived presigned GET URLs for stored files AFTER the calling module has authorized the caller for the
/// file. The location always comes from the <see cref="StoredFile"/> row (never from the client); only VERIFIED files of
/// the expected purpose, in an allowed bucket and under the purpose's key prefix, are signed, pinned to the verified
/// version, served with the verified content type. When configured the object is HEAD-checked first (missing → 410).
/// Logs carry the file id and the outcome only: never the URL, the key or credentials.
/// </summary>
public sealed class FileReadUrlService(IStorageUrlSigner signer, IStorageObjectVerifier verifier, StorageOptions options, TimeProvider time,
    ILogger<FileReadUrlService> logger)
{
    /// <summary>The configured lifetime (bounded to 30–3600 s even if startup validation was bypassed).</summary>
    public TimeSpan MaxTimeToLive => TimeSpan.FromSeconds(Math.Clamp(options.S3.PresignedUrlTtlSeconds,
        S3VerificationOptions.MinPresignedUrlTtlSeconds, S3VerificationOptions.MaxPresignedUrlTtlSeconds));

    public async Task<OperationResult<FileReadUrl>> CreateAsync(StoredFile file, string expectedPurpose, StorageUrlDisposition disposition,
        TimeSpan? requestedTimeToLive, CancellationToken cancellationToken)
    {
        if (file.Status != FileStatuses.Verified || file.VerifiedContentType is null)
            return OperationResult<FileReadUrl>.Fail(FileErrorCodes.NotVerified, "The file is not verified and cannot be served.");
        if (file.Purpose != expectedPurpose || !FilePolicies.ByPurpose.TryGetValue(file.Purpose, out var policy)
            || !policy.AllowedContentTypes.Contains(file.VerifiedContentType) || !options.Buckets().Contains(file.Bucket)
            || !FilePolicies.IsSignableKey(policy, file.ObjectKey))
        {
            logger.LogWarning("File {FileId} refused for a read URL: its stored reference is outside the allowed scope.", file.Id);
            return OperationResult<FileReadUrl>.Fail(FileErrorCodes.NotSignable, "The file cannot be served.");
        }
        if (!signer.IsConfigured)
            return OperationResult<FileReadUrl>.Fail(StorageErrorCodes.NotConfigured, "File storage is not configured.");

        var location = new StorageObjectLocation(file.Bucket, file.ObjectKey, file.ObjectVersionId);
        if (options.S3.VerifyObjectBeforeSigning)
        {
            var head = await verifier.HeadAsync(location, cancellationToken);
            if (head.Status == StorageProbeStatus.NotFound) return Missing(file.Id);
            if (head.Status != StorageProbeStatus.Found) return Unavailable(file.Id);
        }

        var max = MaxTimeToLive;
        var ttl = requestedTimeToLive is { } wanted && wanted > TimeSpan.Zero && wanted < max ? wanted : max;
        var fileName = ContentDispositionHeader.SafeFileName(file.OriginalFileName, file.VerifiedContentType);
        var signed = await signer.CreateReadUrlAsync(location, new StorageUrlRequest(disposition, fileName, file.VerifiedContentType, ttl), cancellationToken);
        switch (signed.Status)
        {
            case StorageUrlStatus.Signed when signed.Url is not null:
                var expiresAt = signed.ExpiresAt ?? time.GetUtcNow().Add(ttl);
                return OperationResult<FileReadUrl>.Ok(new FileReadUrl(signed.Url, expiresAt, disposition, file.VerifiedContentType, fileName));
            case StorageUrlStatus.NotConfigured:
                return OperationResult<FileReadUrl>.Fail(StorageErrorCodes.NotConfigured, "File storage is not configured.");
            case StorageUrlStatus.ObjectMissing:
                return Missing(file.Id);
            default:
                return Unavailable(file.Id);
        }
    }

    private OperationResult<FileReadUrl> Missing(Guid fileId)
    {
        logger.LogWarning("File {FileId}: the verified object is no longer in storage.", fileId);
        return OperationResult<FileReadUrl>.Fail(FileErrorCodes.ObjectMissing, "The file is no longer available in storage.");
    }

    private OperationResult<FileReadUrl> Unavailable(Guid fileId)
    {
        logger.LogWarning("File {FileId}: storage could not be reached to issue a read URL.", fileId);
        return OperationResult<FileReadUrl>.Fail(StorageErrorCodes.Unavailable, "File storage could not be reached; try again later.");
    }
}

/// <summary>
/// RFC 6266 Content-Disposition values with an RFC 5987 <c>filename*</c>. File names are reduced to a base name with no
/// quotes, backslashes, path separators, control characters (CR/LF) or semicolons, so the header cannot be split or
/// injected; the quoted <c>filename</c> is an ASCII-only fallback.
/// </summary>
public static class ContentDispositionHeader
{
    private const int MaxNameLength = 150;

    public static string Build(StorageUrlDisposition disposition, string fileName)
    {
        var safe = Clean(fileName);
        if (safe.Length == 0) safe = "file";
        var ascii = AsciiFallback(safe);
        var type = disposition == StorageUrlDisposition.Attachment ? "attachment" : "inline";
        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{Rfc5987Encode(safe)}";
    }

    /// <summary>Base name, cleaned, at most 150 characters, ending with the content type's extension.</summary>
    public static string SafeFileName(string? originalFileName, string contentType)
    {
        var name = Clean(originalFileName ?? "");
        var extension = FilePolicies.CanonicalExtension(contentType);
        if (extension is not null && !FilePolicies.ExtensionMatches(contentType, name)) name = (name.Length == 0 ? "file" : name) + extension;
        if (name.Length == 0) name = "file";
        if (name.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(name);
            name = name[..(MaxNameLength - ext.Length)] + ext;
        }
        return name;
    }

    private static string Clean(string value)
    {
        // Last segment of either separator, then drop characters that could break or inject header syntax.
        var lastSeparator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (lastSeparator >= 0) value = value[(lastSeparator + 1)..];
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c) || c is '"' or '\\' or '/' or ';' or '%' or '\u2028' or '\u2029') continue;
            builder.Append(c);
        }
        var cleaned = builder.ToString();
        while (cleaned.Contains("..", StringComparison.Ordinal)) cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        return cleaned.Trim().Trim('.').Trim();
    }

    private static string AsciiFallback(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(c is >= ' ' and <= '~' ? c : '_');
        return builder.ToString();
    }

    /// <summary>RFC 5987 attr-char: ALPHA / DIGIT / "!#$&amp;+-.^_`|~"; everything else is UTF-8 percent-encoded.</summary>
    private static string Rfc5987Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '!' or '#' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
                builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
