using System.Text.RegularExpressions;

namespace SmartSolar.Modules.Files;

/// <summary>What a purpose accepts: content types (detected from magic bytes), extensions, size and key prefix.</summary>
public sealed record FilePurposePolicy(
    string Purpose,
    string KeyPrefix,
    IReadOnlyList<string> AllowedContentTypes,
    long MaxSizeBytes,
    bool RequireVerifiedChecksum);

/// <summary>
/// Configuration section "Storage". The frontend uploads; the backend only verifies. Object keys
/// must be <c>{purpose prefix}{uploader user id}/{name}</c>, which ties every object to the identity that
/// registers it and prevents attaching another user's object.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "S3";

    /// <summary>Default bucket used when a registration omits it.</summary>
    public string DefaultBucket { get; set; } = "";

    /// <summary>Buckets the backend accepts references to (comma separated). Empty = only <see cref="DefaultBucket"/>.</summary>
    public string AllowedBuckets { get; set; } = "";

    public S3VerificationOptions S3 { get; set; } = new();

    public IReadOnlyList<string> Buckets()
    {
        var list = AllowedBuckets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!string.IsNullOrWhiteSpace(DefaultBucket) && !list.Contains(DefaultBucket)) list.Add(DefaultBucket);
        return list;
    }
}

/// <summary>Read-only credential used only for verification (HEAD + bounded GET). Empty keys = default AWS credential chain.</summary>
public sealed class S3VerificationOptions
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "ap-southeast-1";
    /// <summary>Custom endpoint for S3-compatible stores (MinIO, R2...). Empty = AWS.</summary>
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>
    /// Only accept objects that carry an S3 version id (bucket versioning enabled), so every reference is
    /// pinned to immutable content. Disable only for stores without versioning, accepting that an upload
    /// to the same key could replace a verified file (detected by the integrity re-check).
    /// </summary>
    public bool RequireObjectVersioning { get; set; } = true;
}

public static partial class FilePolicies
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";

    private const long Mb = 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, FilePurposePolicy> ByPurpose = new Dictionary<string, FilePurposePolicy>
    {
        [FilePurposes.OnSiteSurveyEvidence] = new(FilePurposes.OnSiteSurveyEvidence, "onsite-surveys/", [Jpeg, Png, Webp, Pdf], 15 * Mb, true),
        [FilePurposes.QuotationDocument] = new(FilePurposes.QuotationDocument, "quotations/", [Pdf], 20 * Mb, true),
        [FilePurposes.ContractDocument] = new(FilePurposes.ContractDocument, "contracts/official/", [Pdf], 20 * Mb, true),
        [FilePurposes.SignedContract] = new(FilePurposes.SignedContract, "contracts/signed/", [Pdf], 20 * Mb, true),
        [FilePurposes.InstallationEvidence] = new(FilePurposes.InstallationEvidence, "installations/", [Jpeg, Png, Webp], 15 * Mb, true),
        [FilePurposes.KnowledgeDocument] = new(FilePurposes.KnowledgeDocument, "knowledge/", [Pdf, Docx, PlainText, Markdown], 50 * Mb, true),
        [FilePurposes.ProjectDocument] = new(FilePurposes.ProjectDocument, "project-documents/", [Pdf, Jpeg, Png], 20 * Mb, true),
    };

    private static readonly IReadOnlyDictionary<string, string[]> Extensions = new Dictionary<string, string[]>
    {
        [Pdf] = [".pdf"], [Jpeg] = [".jpg", ".jpeg"], [Png] = [".png"], [Webp] = [".webp"], [Docx] = [".docx"],
        [PlainText] = [".txt"], [Markdown] = [".md", ".markdown"],
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,510}$")]
    private static partial Regex KeyCharacters();

    /// <summary>Rejects traversal, absolute paths, URLs, empty segments and foreign owners.</summary>
    public static string? ValidateKey(FilePurposePolicy policy, string objectKey, Guid uploaderUserId)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !KeyCharacters().IsMatch(objectKey))
            return "The object key contains characters that are not allowed.";
        if (objectKey.Contains("..") || objectKey.Contains("//") || objectKey.EndsWith('/'))
            return "The object key must not contain '..', empty segments or end with '/'.";
        var expected = $"{policy.KeyPrefix}{uploaderUserId:D}/";
        if (!objectKey.StartsWith(expected, StringComparison.Ordinal) || objectKey.Length == expected.Length)
            return $"The object key must start with '{policy.KeyPrefix}{{yourUserId}}/'.";
        return null;
    }

    public static bool ExtensionMatches(string contentType, string fileName)
        => Extensions.TryGetValue(contentType, out var extensions)
           && extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Content type from the first bytes of the object; null when unknown. Text types are accepted only by declared type.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> prefix, string declaredContentType)
    {
        if (prefix.StartsWith("%PDF-"u8)) return Pdf;
        if (prefix.Length >= 3 && prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF) return Jpeg;
        if (prefix.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Png;
        if (prefix.Length >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8)) return Webp;
        if (prefix.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return declaredContentType == Docx ? Docx : null;
        if (declaredContentType is PlainText or Markdown && LooksLikeText(prefix)) return declaredContentType;
        return null;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> prefix)
    {
        foreach (var b in prefix)
        {
            if (b == 0) return false;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) return false;
        }
        return true;
    }
}
