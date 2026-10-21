namespace SmartSolar.Modules.Files;

/// <summary>
/// A reference to an object the frontend uploaded to S3 (or an S3-compatible store). Only the
/// location and metadata are kept: never bytes, never temporary signed URLs. Declared values come
/// from the client and are not trusted; verified values come from the storage provider and are the
/// only ones business rules rely on. Only VERIFIED files may be used as evidence or documents.
/// </summary>
public sealed class StoredFile
{
    public Guid Id { get; set; }
    public string Purpose { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string Bucket { get; set; } = null!;
    public string ObjectKey { get; set; } = null!;
    public string OriginalFileName { get; set; } = null!;
    public string DeclaredContentType { get; set; } = null!;
    public long DeclaredSizeBytes { get; set; }
    public string? DeclaredChecksumSha256 { get; set; }
    public string? ObjectVersionId { get; set; }
    public string? ETag { get; set; }
    public string? VerifiedContentType { get; set; }
    public long? VerifiedSizeBytes { get; set; }
    public string? VerifiedChecksumSha256 { get; set; }
    public string Status { get; set; } = null!;
    public string? RejectionReason { get; set; }
    public int VerificationAttempts { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public int Revision { get; set; }
}

public static class FileStatuses
{
    /// <summary>Registered; the storage provider has not confirmed it yet (or could not be reached/configured).</summary>
    public const string PendingVerification = "PENDING_VERIFICATION";
    public const string Verified = "VERIFIED";
    /// <summary>The object is missing or does not match the declared/allowed metadata. Terminal.</summary>
    public const string Rejected = "REJECTED";
}

public static class FilePurposes
{
    public const string OnSiteSurveyEvidence = "ONSITE_SURVEY_EVIDENCE";
    public const string QuotationDocument = "QUOTATION_DOCUMENT";
    public const string ContractDocument = "CONTRACT_DOCUMENT";
    public const string SignedContract = "SIGNED_CONTRACT";
    public const string InstallationEvidence = "INSTALLATION_EVIDENCE";
    public const string KnowledgeDocument = "KNOWLEDGE_DOCUMENT";
    /// <summary>Permits and approvals backing installation checklist items (grid connection, fire safety…).</summary>
    public const string ProjectDocument = "PROJECT_DOCUMENT";
    /// <summary>After-sales photos: technician visit evidence and customer fault / rejection photos.</summary>
    public const string ServiceEvidence = "SERVICE_EVIDENCE";

    public static readonly IReadOnlyList<string> All =
        [OnSiteSurveyEvidence, QuotationDocument, ContractDocument, SignedContract, InstallationEvidence, KnowledgeDocument, ProjectDocument, ServiceEvidence];
}

public static class FileErrorCodes
{
    public const string InvalidPurpose = "FILE_INVALID_PURPOSE";
    public const string InvalidObjectKey = "FILE_INVALID_OBJECT_KEY";
    public const string BucketNotAllowed = "FILE_BUCKET_NOT_ALLOWED";
    public const string TypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";
    public const string TooLarge = "FILE_TOO_LARGE";
    public const string AlreadyRegistered = "FILE_ALREADY_REGISTERED";
    public const string NotFound = "FILE_NOT_FOUND";
    public const string NotVerified = "FILE_NOT_VERIFIED";
    public const string WrongPurpose = "FILE_WRONG_PURPOSE";
    public const string NotOwned = "FILE_NOT_OWNED";
    public const string AlreadyLinked = "FILE_ALREADY_LINKED";
    public const string ContentChanged = "FILE_CONTENT_CHANGED";
    public const string StorageUnavailable = "FILE_STORAGE_UNAVAILABLE";

    /// <summary>The stored reference cannot be served (wrong purpose, bucket not allowed, key outside its purpose prefix). 409.</summary>
    public const string NotSignable = "FILE_NOT_SIGNABLE";

    /// <summary>The verified object (or its pinned version) no longer exists in storage. 410.</summary>
    public const string ObjectMissing = "FILE_OBJECT_MISSING";
}

/// <summary>Presigned URL outcomes that are not file-specific (both 503).</summary>
public static class StorageErrorCodes
{
    public const string NotConfigured = "STORAGE_NOT_CONFIGURED";
    public const string Unavailable = "STORAGE_UNAVAILABLE";
}
