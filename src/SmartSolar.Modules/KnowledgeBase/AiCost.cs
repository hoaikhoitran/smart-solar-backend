namespace SmartSolar.Modules.KnowledgeBase;

/// <summary>
/// One AI operation in the cost ledger (table <c>ai_usage_ledger</c>): a chat question (its query embedding and, when evidence was found,
/// its generation) or an ingestion run (its document embeddings). A row is created RESERVED with a conservative maximum cost before the
/// provider is called, and settled exactly once: SETTLED with the provider-reported usage, UNCERTAIN when the provider may have charged but
/// reported nothing (timeout, dropped connection, crashed worker: the reservation is kept as the charge until an admin reviews it), or
/// RELEASED when nothing was charged (refused before or by the provider). Settled rows are never rewritten except by the audited review of an
/// UNCERTAIN row. No question text, answer text or key is stored.
/// </summary>
public sealed class AiUsageLedgerEntry
{
    public Guid Id { get; set; }
    /// <summary>Unique per provider operation (e.g. <c>chat:{reservationId}</c>, <c>ingest:{jobId}:{attempt}</c>): a settlement is idempotent.</summary>
    public string IdempotencyKey { get; set; } = null!;
    public string Operation { get; set; } = null!;
    public string Status { get; set; } = null!;
    /// <summary>Why the operation ended this way (<see cref="AiUsageOutcomes"/>).</summary>
    public string Outcome { get; set; } = null!;
    public Guid? UserId { get; set; }
    /// <summary>The caller's highest role when the request was made (Admin, Manager, Sales, Technician, Customer); null for system jobs.</summary>
    public string? UserRole { get; set; }
    public Guid? ChatMessageId { get; set; }
    public Guid? IngestionJobId { get; set; }
    public Guid? VersionId { get; set; }
    public int? ConfigVersion { get; set; }

    public string? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public long EmbeddingTokens { get; set; }
    public decimal EmbeddingCostUsd { get; set; }

    public string? GenerationProvider { get; set; }
    public string? GenerationModel { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public decimal GenerationCostUsd { get; set; }
    /// <summary>False when the provider reported no usage (the cost is the conservative estimate).</summary>
    public bool UsageReported { get; set; } = true;

    /// <summary>Maximum cost reserved before the call (what the budget counted while the operation ran).</summary>
    public decimal ReservedUsd { get; set; }
    /// <summary>Settled cost (embedding + generation). For UNCERTAIN rows it equals <see cref="ReservedUsd"/> until reviewed.</summary>
    public decimal CostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>A RESERVED row past this instant belongs to a crashed or abandoned request and is converted to UNCERTAIN.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}

/// <summary>
/// Atomic spending counters per UTC period (table <c>ai_budget_period</c>): <c>DAY</c> and <c>MONTH</c> (chat), <c>INGEST_MONTH</c>
/// (document embeddings). Every reservation, settlement and expiry updates them under a row lock (taken in a fixed order), so concurrent
/// requests on any number of API instances can never spend past a budget together.
/// </summary>
public sealed class AiBudgetPeriod
{
    public string PeriodKind { get; set; } = null!;
    public DateOnly PeriodStart { get; set; }
    public decimal ReservedUsd { get; set; }
    public decimal SettledUsd { get; set; }
    public decimal UncertainUsd { get; set; }
    public int Requests { get; set; }
    public int Blocked { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Everything that counts against the budget: open reservations, settled cost and unreviewed uncertain charges.</summary>
    public decimal CommittedUsd => ReservedUsd + SettledUsd + UncertainUsd;
}

public static class AiBudgetPeriodKinds
{
    public const string Day = "DAY";
    public const string Month = "MONTH";
    public const string IngestMonth = "INGEST_MONTH";
    public static readonly IReadOnlyList<string> All = [Day, Month, IngestMonth];
}

public static class AiUsageOperations
{
    /// <summary>A chat question: query embedding (+ generation when evidence was found).</summary>
    public const string Chat = "CHAT";
    /// <summary>Document embeddings of an ingestion / reindex run.</summary>
    public const string Ingestion = "INGESTION";
    public static readonly IReadOnlyList<string> All = [Chat, Ingestion];
}

public static class AiUsageStatuses
{
    public const string Reserved = "RESERVED";
    public const string Settled = "SETTLED";
    public const string Uncertain = "UNCERTAIN";
    public const string Released = "RELEASED";
    public static readonly IReadOnlyList<string> All = [Reserved, Settled, Uncertain, Released];
}

public static class AiUsageOutcomes
{
    public const string Pending = "PENDING";
    public const string Answered = "ANSWERED";
    public const string InsufficientContext = "INSUFFICIENT_CONTEXT";
    public const string ProviderError = "PROVIDER_ERROR";
    public const string ProviderRateLimited = "PROVIDER_RATE_LIMITED";
    public const string Timeout = "TIMEOUT";
    public const string Busy = "BUSY";
    public const string Expired = "EXPIRED";
    public const string Indexed = "INDEXED";
    public const string IngestFailed = "INGEST_FAILED";
    public const string Reviewed = "REVIEWED";
    public static readonly IReadOnlyList<string> All =
        [Pending, Answered, InsufficientContext, ProviderError, ProviderRateLimited, Timeout, Busy, Expired, Indexed, IngestFailed, Reviewed];
}

/// <summary>Stable error codes of AI admission (HTTP 429 with retry guidance, except the switch which is 503).</summary>
public static class AiCostErrorCodes
{
    public const string BudgetExceeded = "AI_BUDGET_EXCEEDED";
    public const string UserQuotaExceeded = "AI_USER_QUOTA_EXCEEDED";
    public const string Concurrency = "AI_CONCURRENCY_LIMIT";
    /// <summary>The deployment-wide daily token ceiling (Rag:GlobalDailyTokens) is reached (reported to clients as RAG_QUOTA_EXCEEDED).</summary>
    public const string GlobalTokenQuota = "AI_GLOBAL_TOKEN_QUOTA";
    public const string GlobalBusy = "AI_GLOBAL_CONCURRENCY_LIMIT";
    public const string RequestInProgress = "AI_REQUEST_IN_PROGRESS";
    public const string GenerationDisabled = "AI_GENERATION_DISABLED";
    public const string UsageNotFound = "AI_USAGE_NOT_FOUND";
    public const string InvalidReview = "AI_USAGE_REVIEW_INVALID";
}

/// <summary>Prices of one call, per 1M tokens (USD). A local or offline provider costs nothing.</summary>
public sealed record AiPrice(decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion)
{
    public static readonly AiPrice Free = new(0, 0, 0);
}

/// <summary>
/// Deterministic USD arithmetic for the ledger: 8 decimal places (a query embedding costs about 0.00000024 USD, which 6 places would round to
/// zero), rounded half away from zero for settlements and up for reservations.
/// </summary>
public static class AiCostCalculator
{
    public const int Scale = 8;
    /// <summary>Tokens per character assumed for estimates (Vietnamese: ≥ 1 token per 3 characters; rounded up).</summary>
    public const int CharactersPerToken = 3;
    /// <summary>System prompt, source markup, citations instructions (measured upper bound of prompt v2: ~900 tokens) plus per-message overhead.</summary>
    public const int PromptOverheadTokens = 1200;
    /// <summary>Upper bound of the history the RAG service keeps (4000 characters).</summary>
    public const int HistoryTokenBound = 4000 / CharactersPerToken + 1;

    public static decimal Generation(AiPrice price, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(0, inputTokens));
        var usd = ((inputTokens - cached) * price.InputPerMillion + cached * price.CachedInputPerMillion + Math.Max(0, outputTokens) * price.OutputPerMillion) / 1_000_000m;
        return Round(usd);
    }

    public static decimal Embedding(decimal inputPerMillion, long tokens) => Round(Math.Max(0, tokens) * inputPerMillion / 1_000_000m);

    public static long EstimateTokens(int characters) => (Math.Max(0, characters) + CharactersPerToken - 1) / CharactersPerToken + 1;

    /// <summary>
    /// Maximum cost of one question: the question embedding plus a generation that fills the whole context budget, the history bound, the
    /// prompt overhead and the question, and spends the whole output budget (reasoning tokens are part of the output budget). Cached
    /// input is never assumed.
    /// </summary>
    public static decimal MaxChat(AiPrice generation, decimal embeddingPerMillion, int questionCharacters, int maxContextTokens, int maxOutputTokens)
    {
        var question = EstimateTokens(questionCharacters);
        var input = question + maxContextTokens + HistoryTokenBound + PromptOverheadTokens;
        return RoundUp(Embedding(embeddingPerMillion, question) + (input * generation.InputPerMillion + maxOutputTokens * generation.OutputPerMillion) / 1_000_000m);
    }

    /// <summary>Maximum embedding cost of a document: every extracted character up to the RAG service's limit, at one token per 3 characters.</summary>
    public static decimal MaxIngestion(decimal embeddingPerMillion, long maxCharacters)
        => RoundUp((Math.Max(0, maxCharacters) / CharactersPerToken + 1) * embeddingPerMillion / 1_000_000m);

    public static decimal Round(decimal usd) => decimal.Round(usd, Scale, MidpointRounding.AwayFromZero);

    public static decimal RoundUp(decimal usd) => Math.Ceiling(usd * 100_000_000m) / 100_000_000m;
}

/// <summary>What an AI admission is asked to reserve (all limits come from the effective configuration).</summary>
public sealed record AiAdmissionRequest(
    Guid UserId, string? UserRole, DateOnly Date, long ReservedTokens, decimal ReservedUsd, bool Billable, string? ClientMessageId, int ConfigVersion,
    int UserRequestsPerDay, int UserConcurrency, int GlobalConcurrency, decimal DailyBudgetUsd, decimal MonthlyBudgetUsd, DateTimeOffset Now,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The admission decision. <see cref="Limit"/> is null when admitted, otherwise one of <see cref="AiCostErrorCodes"/>; <see cref="RetryAfter"/>
/// tells the client when trying again can succeed.
/// </summary>
public sealed record AiAdmission(Guid? ReservationId, string? Limit, TimeSpan? RetryAfter = null)
{
    public bool Admitted => ReservationId is not null;
}

/// <summary>What a chat question actually used (from the RAG service response, or nothing when it failed before reporting).</summary>
public sealed record AiChatSettlement(
    string Outcome, bool Uncertain, Guid? ChatMessageId, string? EmbeddingProvider, string? EmbeddingModel, long EmbeddingTokens, decimal EmbeddingCostUsd,
    string? GenerationProvider, string? GenerationModel, long InputTokens, long CachedInputTokens, long OutputTokens, long ReasoningTokens,
    decimal GenerationCostUsd, bool UsageReported, int LatencyMs, DateTimeOffset Now)
{
    public decimal CostUsd => EmbeddingCostUsd + GenerationCostUsd;
}
