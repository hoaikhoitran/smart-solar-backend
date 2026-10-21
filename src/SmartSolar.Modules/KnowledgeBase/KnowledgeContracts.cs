using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed class RagOptions
{
    public const string SectionName = "Rag";
    public bool Enabled { get; set; }
    /// <summary>Internal URL of the FastAPI service (Docker network), e.g. http://rag-service:8000.</summary>
    public string ServiceBaseUrl { get; set; } = "http://rag-service:8000";
    /// <summary>Shared secret for HMAC request signing between the API and the RAG service (environment only).</summary>
    public string? ServiceSharedSecret { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int IngestTimeoutSeconds { get; set; } = 300;
    public int MaxQuestionCharacters { get; set; } = 8000;
    public int MaxHistoryMessages { get; set; } = 6;
    public int TopK { get; set; } = 6;
    public int MaxTopK { get; set; } = 10;
    public int MaxContextTokens { get; set; } = 3000;
    public int MaxOutputTokens { get; set; } = 700;
    public int MaxConcurrentGenerationsPerUser { get; set; } = 2;
    public int DailyRequestsPerUser { get; set; } = 100;
    public long DailyTokensPerUser { get; set; } = 200_000;
    public long GlobalDailyTokens { get; set; } = 5_000_000;
    public int MaxIngestAttempts { get; set; } = 5;
    public bool IngestionWorkerEnabled { get; set; } = true;
    public int IngestionPollSeconds { get; set; } = 15;

    // ---- Bootstrap values: the effective configuration (version 0) while no admin configuration is ACTIVE.
    // TopK, MaxContextTokens, MaxOutputTokens and MaxHistoryMessages above are bootstrap values too.

    public string GenerationModel { get; set; } = "gpt-4.1-mini";
    /// <summary>Ignored (sent as null) when the bootstrap model does not support temperature.</summary>
    public decimal? Temperature { get; set; } = 0.1m;
    public int ChunkCharacters { get; set; } = 2400;
    public int ChunkOverlap { get; set; } = 300;
    public decimal MinScore { get; set; } = 0.2m;
    public string PromptVersion { get; set; } = "v1";

    // ---- Admin runtime configuration (rag_configuration): allowlists and bounds. No secrets, no URLs.

    /// <summary>Allowlist of generation models admins may choose. Empty = the built-in defaults.</summary>
    public List<RagGenerationModelOptions> GenerationModels { get; set; } = [];
    /// <summary>Prompt template versions known to the RAG service (it holds the templates). Empty = ["v1"].</summary>
    public List<string> PromptVersions { get; set; } = [];
    /// <summary>Bounds for admin values; they can only narrow <see cref="RagConfigurationLimits.Hard"/>.</summary>
    public RagConfigurationLimits ConfigurationLimits { get; set; } = new();
    /// <summary>How long the effective configuration is cached per instance (clamped to 0–15 s; activation invalidates it locally).</summary>
    public int ConfigurationCacheSeconds { get; set; } = 10;
}

/// <param name="ConfigVersion">Configuration snapshot of the job (0 = bootstrap).</param>
public sealed record RagIngestRequest(Guid VersionId, Guid DocumentId, string Bucket, string ObjectKey, string? ObjectVersionId, string ContentType,
    string ExpectedSha256, string Title, int VersionNumber, int ConfigVersion = 0, int? ChunkCharacters = null, int? ChunkOverlap = null);

/// <param name="ConfigVersion">Echoed by the RAG service with the chunk settings it actually used (null from an older service).</param>
public sealed record RagIngestResult(bool Reached, string Status, int? PageCount, int? ChunkCount, int? Characters, string? EmbeddingModel, int? EmbeddingDimension, string? Error,
    int? ConfigVersion = null, int? ChunkCharacters = null, int? ChunkOverlap = null);

public sealed record RagHistoryTurn(string Role, string Content);

public sealed record RagAllowedVersion(Guid VersionId, Guid DocumentId, string Title, int VersionNumber);

/// <summary>
/// One question. Generation values come from the effective configuration; null model / token parameter / min score / prompt
/// version mean "service default". With a model named (an ACTIVE admin configuration), a null <paramref name="Temperature"/> is
/// sent as JSON null: the service then omits it from the provider call. Without a model (bootstrap) model, temperature and token
/// parameter are not sent at all, so the service's environment defaults (RAG_LLM_MODEL, RAG_LLM_TEMPERATURE) apply.
/// </summary>
public sealed record RagAnswerRequest(string Question, IReadOnlyList<RagHistoryTurn> History, IReadOnlyList<RagAllowedVersion> AllowedVersions,
    int TopK, int MaxContextTokens, int MaxOutputTokens, int ConfigVersion = 0, string? GenerationModel = null, decimal? Temperature = null,
    string? TokenParameter = null, decimal? MinScore = null, string? PromptVersion = null);

public sealed record RagCitation(Guid DocumentId, Guid VersionId, string ChunkId, int? Page, string? Section, double Score, string? Snippet);

public sealed record RagAnswerResult(bool Reached, string? Error, string? Answer, bool Grounded, IReadOnlyList<RagCitation> Citations,
    int PromptTokens, int CompletionTokens, string? Model, int RetrievalMs, int GenerationMs, int? ConfigVersion = null);

/// <summary>Internal client of the FastAPI RAG service (HMAC-signed requests over the private network).</summary>
public interface IRagServiceClient
{
    Task<RagIngestResult> IngestAsync(RagIngestRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteVectorsAsync(Guid versionId, CancellationToken cancellationToken);
    Task<RagAnswerResult> AnswerAsync(RagAnswerRequest request, CancellationToken cancellationToken);
}

public interface IKnowledgeUnitOfWork : ITransactionalUnitOfWork
{
    Task<KnowledgeDocument?> FindDocumentAsync(Guid documentId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string? status, string? category, CancellationToken cancellationToken);
    Task<KnowledgeDocumentVersion?> FindVersionAsync(Guid versionId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<KnowledgeDocumentVersion>> GetVersionsAsync(Guid documentId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<KnowledgeIngestionJob>> GetJobsAsync(Guid documentId, CancellationToken cancellationToken);
    Task<KnowledgeIngestionJob?> FindJobAsync(Guid jobId, bool track, CancellationToken cancellationToken);
    Task<int> CountJobsAsync(Guid versionId, string kind, CancellationToken cancellationToken);

    /// <summary>Conditional lease (single UPDATE): true when this caller now owns the job.</summary>
    /// <summary>
    /// Leases a due job (pending, waiting for retry, or running with an expired lease) that still has
    /// attempts left, counting the attempt now so a crashed run is never free. <paramref name="leaseUntil"/>
    /// doubles as the fencing token for <see cref="TryReleaseLeaseAsync"/> and outcome recording.
    /// </summary>
    Task<bool> TryLeaseJobAsync(Guid jobId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    /// <summary>Marks a due job whose attempts are used up (for example after repeated crashes) as FAILED.</summary>
    Task<bool> TryFailExhaustedJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Gives a lease back without using the attempt (service shutdown). No-op if the lease is no longer ours.</summary>
    Task<bool> TryReleaseLeaseAsync(Guid jobId, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Exactly the versions the chatbot may read right now: published version of each published document.</summary>
    Task<IReadOnlyList<RagAllowedVersion>> GetRetrievableVersionsAsync(CancellationToken cancellationToken);

    Task<ChatSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int? lastCount, CancellationToken cancellationToken);
    Task<ChatMessage?> FindMessageByClientIdAsync(Guid sessionId, string clientMessageId, CancellationToken cancellationToken);
    Task<ChatMessage?> FindReplyAsync(Guid userMessageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatCitation>> GetCitationsAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically reserves one request, <paramref name="tokens"/> tokens and one generation slot for the
    /// user, and the tokens in the global ledger. Returns null on success or the limit that was hit.
    /// <summary>
    /// In one transaction (user and global ledger rows locked, global first): converts the user's expired
    /// reservations into charged estimates, checks the concurrency slots and the daily request/token
    /// allowances, and either creates a reservation lasting until <paramref name="expiresAt"/> or reports
    /// the limit hit ("CONCURRENCY", "USER_QUOTA" or "GLOBAL_QUOTA"). Nothing is written when refused.
    /// </summary>
    Task<UsageReservation> TryReserveUsageAsync(Guid userId, DateOnly date, long tokens, RagOptions limits, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a reservation by the real usage, in one transaction. Idempotent: returns false (and
    /// charges nothing) if the reservation was already completed or already charged at its estimate.
    /// </summary>
    Task<bool> CompleteUsageAsync(Guid reservationId, long inputTokens, long outputTokens, DateTimeOffset now, CancellationToken cancellationToken);

    // ---- Admin RAG configuration

    /// <summary>The single ACTIVE or DRAFT configuration (partial unique indexes guarantee at most one of each).</summary>
    Task<RagConfiguration?> FindConfigurationAsync(string status, bool track, CancellationToken cancellationToken);
    Task<RagConfiguration?> FindConfigurationByVersionAsync(int version, bool track, CancellationToken cancellationToken);
    Task<int> MaxConfigurationVersionAsync(CancellationToken cancellationToken);
    Task<(IReadOnlyList<RagConfiguration> Items, int Total)> ListConfigurationsAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>READY_FOR_REVIEW and PUBLISHED versions of documents that are not archived (the versions that hold live chunks).</summary>
    Task<IReadOnlyList<KnowledgeDocumentVersion>> GetIndexedVersionsAsync(bool track, CancellationToken cancellationToken);

    /// <summary>Versions with a PENDING, RUNNING or RETRY_WAITING job of any kind.</summary>
    Task<IReadOnlyList<Guid>> GetVersionsWithOpenJobsAsync(CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
}

/// <summary>Outcome of <see cref="IKnowledgeUnitOfWork.TryReserveUsageAsync"/>: a reservation, or the limit that was hit.</summary>
public sealed record UsageReservation(Guid? ReservationId, string? Limit)
{
    public bool Reserved => ReservationId is not null;
}
