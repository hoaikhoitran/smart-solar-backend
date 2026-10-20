namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>
/// A knowledge-base document managed by Admins. Its content lives in versions; only the version
/// referenced by <see cref="PublishedVersionId"/> of a PUBLISHED document is ever retrievable.
/// Unpublishing or archiving clears that reference, so the document disappears from answers
/// immediately (vector cleanup runs asynchronously).
/// </summary>
public sealed class KnowledgeDocument
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string Category { get; set; } = null!;
    /// <summary>Comma-separated, normalised tags.</summary>
    public string? Tags { get; set; }
    public string Status { get; set; } = null!;
    public Guid? LatestVersionId { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class KnowledgeDocumentVersion
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; }
    public Guid FileId { get; set; }
    public string Status { get; set; } = null!;
    public int? PageCount { get; set; }
    public int? ChunkCount { get; set; }
    public int? ExtractedCharacters { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDimension { get; set; }
    public string? FailureReason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? IngestedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>RAG configuration version whose chunk settings produced the live chunks (set when an ingest returns READY).</summary>
    public int? IndexedConfigVersion { get; set; }
    public int? IndexedChunkCharacters { get; set; }
    public int? IndexedChunkOverlap { get; set; }
    /// <summary>
    /// Outcome of the most recent ingest attempt (READY, FAILED, OCR_REQUIRED, UNSUPPORTED_CONTENT, INTEGRITY_FAILED).
    /// A published version whose reindex fails keeps its status and its old chunks; only this outcome records the failure.
    /// </summary>
    public string? LastIngestStatus { get; set; }
    public string? LastIngestError { get; set; }
    public int Revision { get; set; }
}

/// <summary>Durable ingestion work item, leased by one worker at a time and retried with backoff.</summary>
public sealed class KnowledgeIngestionJob
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public string Kind { get; set; } = null!;
    public string Status { get; set; } = null!;
    /// <summary>"{versionId}:{kind}:{generation}" — the same request is never queued twice.</summary>
    public string IdempotencyKey { get; set; } = null!;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>
    /// Snapshot of the effective RAG configuration taken when an INGEST job is queued (null for DELETE_VECTORS):
    /// the job always runs with these chunk settings, even if another configuration is activated meanwhile.
    /// </summary>
    public int? ConfigVersion { get; set; }
    public int? ChunkCharacters { get; set; }
    public int? ChunkOverlap { get; set; }
}

public sealed class ChatSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Role { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string Status { get; set; } = null!;
    /// <summary>Client-supplied id of the user message; a retried request returns the stored answer.</summary>
    public string? ClientMessageId { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public string? Model { get; set; }
    public int? LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChatCitation
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public int Rank { get; set; }
    public Guid DocumentId { get; set; }
    public Guid VersionId { get; set; }
    public string ChunkId { get; set; } = null!;
    public string DocumentTitle { get; set; } = null!;
    public int VersionNumber { get; set; }
    public int? PageNumber { get; set; }
    public string? Section { get; set; }
    public double Score { get; set; }
    public string? Snippet { get; set; }
}

/// <summary>
/// Per-user (and, with <see cref="Guid.Empty"/>, global) daily usage ledger. Counters only ever grow:
/// <see cref="InputTokens"/>/<see cref="OutputTokens"/> are the provider-reported usage of completed
/// questions; <see cref="EstimatedTokens"/> is what was reserved for questions whose reservation expired
/// without completing (crashed or abandoned requests are charged at their estimate, never forgiven).
/// Open reservations live in <see cref="RagUsageReservation"/>.
/// </summary>
public sealed class RagUsageDaily
{
    public Guid UserId { get; set; }
    public DateOnly UsageDate { get; set; }
    public int Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long EstimatedTokens { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public long ChargedTokens => InputTokens + OutputTokens + EstimatedTokens;
}

/// <summary>
/// An in-flight question: its token estimate counts against the daily allowance and it holds one of the
/// user's concurrent-generation slots until it completes or <see cref="ExpiresAt"/> passes (lease), so a
/// crashed API instance can never leave a slot taken.
/// </summary>
public sealed class RagUsageReservation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly UsageDate { get; set; }
    public long ReservedTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public static class DocumentStatuses
{
    public const string Registered = "REGISTERED";
    public const string Processing = "PROCESSING";
    public const string ReadyForReview = "READY_FOR_REVIEW";
    public const string Published = "PUBLISHED";
    public const string Unpublished = "UNPUBLISHED";
    public const string Failed = "FAILED";
    public const string Archived = "ARCHIVED";
    public static readonly IReadOnlyList<string> All = [Registered, Processing, ReadyForReview, Published, Unpublished, Failed, Archived];
}

public static class VersionStatuses
{
    public const string Registered = "REGISTERED";
    public const string Verifying = "VERIFYING";
    public const string Processing = "PROCESSING";
    public const string ReadyForReview = "READY_FOR_REVIEW";
    public const string Published = "PUBLISHED";
    public const string Superseded = "SUPERSEDED";
    public const string Failed = "FAILED";
    public const string OcrRequired = "OCR_REQUIRED";
    public const string UnsupportedContent = "UNSUPPORTED_CONTENT";
    public const string Withdrawn = "WITHDRAWN";
    public static readonly IReadOnlyList<string> All = [Registered, Verifying, Processing, ReadyForReview, Published, Superseded, Failed, OcrRequired, UnsupportedContent, Withdrawn];
}

public static class JobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string RetryWaiting = "RETRY_WAITING";
    public static readonly IReadOnlyList<string> All = [Pending, Running, Completed, Failed, RetryWaiting];
}

public static class JobKinds
{
    public const string Ingest = "INGEST";
    public const string DeleteVectors = "DELETE_VECTORS";
}

public static class ChatRoles
{
    public const string User = "USER";
    public const string Assistant = "ASSISTANT";
}

public static class ChatMessageStatuses
{
    public const string Completed = "COMPLETED";
    public const string InsufficientContext = "INSUFFICIENT_CONTEXT";
    public const string Failed = "FAILED";
}

public static class KnowledgeErrorCodes
{
    public const string DocumentNotFound = "RAG_DOCUMENT_NOT_FOUND";
    public const string VersionNotFound = "RAG_VERSION_NOT_FOUND";
    public const string InvalidState = "RAG_INVALID_STATE";
    public const string StaleRevision = "RAG_STALE_REVISION";
    public const string SessionNotFound = "RAG_SESSION_NOT_FOUND";
    public const string MessageTooLong = "RAG_MESSAGE_TOO_LONG";
    public const string QuotaExceeded = "RAG_QUOTA_EXCEEDED";
    public const string TooManyConcurrent = "RAG_TOO_MANY_CONCURRENT_REQUESTS";
    public const string ServiceUnavailable = "RAG_SERVICE_UNAVAILABLE";
    public const string JobNotFound = "RAG_JOB_NOT_FOUND";
}

/// <summary>Error codes of the admin RAG configuration (HTTP mapping: *_VALIDATION_* → 400, *_NOT_FOUND → 404, others → 409).</summary>
public static class RagConfigurationErrorCodes
{
    public const string ValidationFailed = "RAG_CONFIG_VALIDATION_FAILED";
    public const string StaleRevision = "RAG_CONFIG_STALE_REVISION";
    public const string DraftNotFound = "RAG_CONFIG_DRAFT_NOT_FOUND";
    public const string VersionNotFound = "RAG_CONFIG_VERSION_NOT_FOUND";
    public const string InvalidState = "RAG_CONFIG_INVALID_STATE";
}
