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
    /// <summary>
    /// The RAG service's embedding model (its RAG_EMBEDDING_MODEL), optional. When set, reindex-stale also queues every indexed version
    /// whose chunks were embedded with another model (retrieval only reads chunks of the configured model, so those are invisible).
    /// </summary>
    public string? EmbeddingModel { get; set; }
    public int MaxQuestionCharacters { get; set; } = RagCostDefaults.MaxQuestionCharacters;
    public int MaxHistoryMessages { get; set; } = 6;
    public int TopK { get; set; } = 6;
    public int MaxTopK { get; set; } = 10;
    public int MaxContextTokens { get; set; } = RagCostDefaults.MaxContextTokens;
    public int MaxOutputTokens { get; set; } = RagCostDefaults.MaxOutputTokens;
    public int MaxConcurrentGenerationsPerUser { get; set; } = RagCostDefaults.UserConcurrency;
    public int DailyRequestsPerUser { get; set; } = RagCostDefaults.UserRequestsPerDay;
    /// <summary>Bootstrap cost controls (an ACTIVE configuration version replaces them; hard ceilings in <see cref="RagConfigurationLimits.Hard"/>).</summary>
    public bool GenerationEnabled { get; set; } = true;
    public int GlobalConcurrentGenerations { get; set; } = RagCostDefaults.GlobalConcurrency;
    public decimal DailyBudgetUsd { get; set; } = RagCostDefaults.DailyBudgetUsd;
    public decimal MonthlyBudgetUsd { get; set; } = RagCostDefaults.MonthlyBudgetUsd;
    public decimal MonthlyIngestionBudgetUsd { get; set; } = RagCostDefaults.MonthlyIngestionBudgetUsd;
    /// <summary>Upper bound of characters the RAG service extracts from one document (its RAG_MAX_CHARACTERS): bounds an ingestion estimate.</summary>
    public long MaxDocumentCharacters { get; set; } = 3_000_000;
    /// <summary>Embedding models and their prices. Empty = the built-in list (<see cref="RagConfigurationRules.DefaultEmbeddingModels"/>).</summary>
    public List<RagEmbeddingModelOptions> EmbeddingModels { get; set; } = [];
    public long DailyTokensPerUser { get; set; } = 200_000;
    public long GlobalDailyTokens { get; set; } = 5_000_000;
    public int MaxIngestAttempts { get; set; } = 5;
    public bool IngestionWorkerEnabled { get; set; } = true;
    public int IngestionPollSeconds { get; set; } = 15;

    // ---- Bootstrap values: the effective configuration (version 0) while no admin configuration is ACTIVE.
    // TopK, MaxContextTokens, MaxOutputTokens and MaxHistoryMessages above are bootstrap values too.

    /// <summary>Bootstrap generation model; empty = the least expensive usable model of the registry.</summary>
    public string GenerationModel { get; set; } = "";
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
/// <param name="EmbeddingTokens">Billable embedding tokens the run spent (also when it failed after embedding); null from an older service.</param>
/// <param name="Billing">"uncertain" when a failed provider call may still have been charged; "none" otherwise.</param>
public sealed record RagIngestResult(bool Reached, string Status, int? PageCount, int? ChunkCount, int? Characters, string? EmbeddingModel, int? EmbeddingDimension, string? Error,
    int? ConfigVersion = null, int? ChunkCharacters = null, int? ChunkOverlap = null, long? EmbeddingTokens = null, string? EmbeddingProvider = null,
    string Billing = "none");

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
    string? TokenParameter = null, decimal? MinScore = null, string? PromptVersion = null, string? ReasoningEffort = null, int? TimeoutSeconds = null);

public sealed record RagCitation(Guid DocumentId, Guid VersionId, string ChunkId, int? Page, string? Section, double Score, string? Snippet);

/// <summary>
/// The RAG service's answer. Cost fields: the question embedding (<see cref="EmbeddingTokens"/> of <see cref="EmbeddingProvider"/>), the
/// generation (null provider = the model was not called), cached and reasoning tokens, and whether usage was reported. For a failure,
/// <see cref="Billing"/> is "uncertain" when the provider may have charged it.
/// </summary>
public sealed record RagAnswerResult(bool Reached, string? Error, string? Answer, bool Grounded, IReadOnlyList<RagCitation> Citations,
    int PromptTokens, int CompletionTokens, string? Model, int RetrievalMs, int GenerationMs, int? ConfigVersion = null,
    long EmbeddingTokens = 0, string? EmbeddingModel = null, string? EmbeddingProvider = null, string? GenerationProvider = null,
    int CachedPromptTokens = 0, int ReasoningTokens = 0, bool UsageReported = true, string Billing = "none", int ContextMs = 0);

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

    /// <summary>One page of documents (latest update first, id tie-breaker), filtered in SQL, with the total.</summary>
    Task<(IReadOnlyList<KnowledgeDocument> Rows, int Total)> PageDocumentsAsync(string? status, string? category, int skip, int take, CancellationToken cancellationToken);
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

    /// <summary>
    /// Inside an open transaction: true when the job is still RUNNING under exactly <paramref name="leaseUntil"/>; the row then stays
    /// locked until the transaction ends (outcome recording is fenced by this).
    /// </summary>
    Task<bool> TryHoldLeaseAsync(Guid jobId, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    /// <summary>Gives a lease back without using the attempt (service shutdown). No-op if the lease is no longer ours.</summary>
    Task<bool> TryReleaseLeaseAsync(Guid jobId, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Exactly the versions the chatbot may read right now: published version of each published document.</summary>
    Task<IReadOnlyList<RagAllowedVersion>> GetRetrievableVersionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The retrievable versions a Customer may read: published documents an Admin classified as customer-visible (tag
    /// <c>knowledge_document.audience = CUSTOMER</c>). Every other document is internal.
    /// </summary>
    Task<IReadOnlyList<RagAllowedVersion>> GetCustomerRetrievableVersionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeDocumentAudienceChange>> GetAudienceChangesAsync(Guid documentId, CancellationToken cancellationToken);

    Task<ChatSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int? lastCount, CancellationToken cancellationToken);
    Task<ChatMessage?> FindMessageByClientIdAsync(Guid sessionId, string clientMessageId, CancellationToken cancellationToken);

    /// <summary>The user's question with this client message id in any of their sessions (a retried first question has no session id yet).</summary>
    Task<ChatMessage?> FindUserMessageByClientIdAsync(Guid userId, string clientMessageId, CancellationToken cancellationToken);
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

    // ---- AI cost control (2026-11-17): USD budgets, quotas and concurrency, enforced atomically in PostgreSQL ----

    /// <summary>
    /// One atomic admission for a chat question: under the ledger row locks (usage rows of the day, then the budget periods in a fixed
    /// order) it checks the per-user concurrency and daily requests, the token quotas of <paramref name="limits"/>, the global concurrency of
    /// billable requests and the daily / monthly USD budgets against everything committed (reserved + settled + uncertain), then reserves
    /// the request (tokens, USD, ledger row RESERVED). A concurrent duplicate of the same client message id is refused by a unique index
    /// (<see cref="PersistenceConflictException"/>). Refusals are counted on the day's budget period.
    /// </summary>
    Task<AiAdmission> TryAdmitAsync(AiAdmissionRequest request, RagOptions limits, CancellationToken cancellationToken);

    /// <summary>Settles an admitted question exactly once (tokens to the daily ledger, cost to the budget periods, ledger row SETTLED / UNCERTAIN / RELEASED).</summary>
    Task<bool> SettleChatAsync(Guid reservationId, long inputTokens, long outputTokens, AiChatSettlement settlement, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves the maximum embedding cost of one ingestion attempt against the monthly ingestion budget (idempotent per job attempt).
    /// Returns the ledger id, or the refusal.
    /// </summary>
    Task<AiAdmission> TryReserveIngestionAsync(Guid jobId, int attempt, Guid versionId, decimal reservedUsd, decimal monthlyBudgetUsd, int configVersion,
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<bool> SettleIngestionAsync(Guid ledgerId, string outcome, bool uncertain, string? provider, string? model, long tokens, decimal costUsd, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>RESERVED ledger rows past their lease (crashed or abandoned work) become UNCERTAIN at their reserved cost. Returns how many.</summary>
    Task<int> ExpireAiReservationsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Admin review of an UNCERTAIN row: settle it at the actual cost from the provider's billing (0 = it was not charged).</summary>
    Task<bool> ReviewUncertainAsync(Guid ledgerId, decimal costUsd, Guid adminUserId, string note, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The budget counters of the given periods (absent periods are returned empty).</summary>
    Task<IReadOnlyList<AiBudgetPeriod>> GetBudgetPeriodsAsync(IReadOnlyCollection<(string Kind, DateOnly Start)> periods, CancellationToken cancellationToken);

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
