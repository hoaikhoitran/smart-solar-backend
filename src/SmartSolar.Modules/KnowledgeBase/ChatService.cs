using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed record CitationView(int Rank, Guid DocumentId, string DocumentTitle, Guid VersionId, int VersionNumber, string ChunkId, int? Page, string? Section,
    double Score, string? Snippet);

/// <summary>
/// <see cref="Withheld"/>: a LEGACY assistant answer whose text a customer may not read (generated before audiences existed, or by the former
/// staff retrieval that could use internal documents), shown as <see cref="ChatService.WithheldAnswer"/> without citations. The stored text is
/// kept for review. Citations are shown only while their source version is still retrievable (published and approved for all signed-in roles).
/// </summary>
public sealed record ChatMessageView(Guid MessageId, string Role, string Content, string Status, DateTimeOffset CreatedAt, IReadOnlyList<CitationView> Citations,
    bool Withheld = false);

/// <param name="Advisor">The structured configuration advice when the question was answered by the deterministic advisor (null otherwise).</param>
/// <param name="InsufficientReason">Why a knowledge answer is not grounded (null when grounded or for the advisor): see <see cref="InsufficientReasons"/>.</param>
public sealed record ChatAnswerView(Guid SessionId, ChatMessageView Question, ChatMessageView Answer, bool Grounded, string Notice,
    Advisor.SolarAdvisorView? Advisor = null, string? InsufficientReason = null);

/// <summary>Safe, content-free reasons for an INSUFFICIENT_CONTEXT answer (identical for every role: retrieval has no role filter).</summary>
public static class InsufficientReasons
{
    /// <summary>No published version is approved for all signed-in roles (nothing can be retrieved by anyone).</summary>
    public const string NoPublishedDocuments = "NO_PUBLISHED_DOCUMENTS";
    /// <summary>The allowed versions have no chunks for the query embedding model (empty documents or a reindex is required).</summary>
    public const string NoIndexedChunks = "NO_INDEXED_CHUNKS";
    /// <summary>Chunks exist but none reaches the configured minimum score for this question.</summary>
    public const string BelowMinScore = "BELOW_MIN_SCORE";
    /// <summary>The model found no answer supported by the provided sources (or cited none).</summary>
    public const string ModelInsufficient = "MODEL_INSUFFICIENT";
    /// <summary>A cited version stopped being retrievable while the answer was generated (unpublished / superseded meanwhile).</summary>
    public const string CitationsRevoked = "CITATIONS_REVOKED";
    /// <summary>The RAG service did not say why (older service).</summary>
    public const string Unspecified = "UNSPECIFIED";
}

public sealed record ChatSessionView(Guid SessionId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatSessionDetailView(ChatSessionView Session, IReadOnlyList<ChatMessageView> Messages);

/// <summary>
/// The reader class for LEGACY chat history only (answers stored before the shared publication scope). It no longer changes what is
/// retrieved: every signed-in role retrieves the same approved published versions. <see cref="Customer"/> is the restricted default.
/// </summary>
public enum KnowledgeAudience
{
    /// <summary>A customer (or a token without a staff role): legacy INTERNAL / pre-audience answers stay withheld.</summary>
    Customer,
    /// <summary>Signed-in staff: may read the text of their own legacy answers.</summary>
    Staff,
}

/// <summary>
/// Persisted LEGACY audience of a knowledge document (<c>knowledge_document.audience</c>: INTERNAL / CUSTOMER, no longer used for retrieval)
/// and the retrieval scope of a generated answer (<c>chat_message.retrieval_audience</c>: <see cref="Shared"/> for every new answer).
/// </summary>
public static class KnowledgeAudiences
{
    /// <summary>Legacy: staff only. The default, and the value of every document that existed before the column.</summary>
    public const string Internal = "INTERNAL";
    /// <summary>Legacy: customers (and staff) could retrieve it.</summary>
    public const string Customer = "CUSTOMER";
    /// <summary>Retrieval scope of answers since 2026-11-23: only versions an Admin approved for all signed-in roles.</summary>
    public const string Shared = "ALL_SIGNED_IN_ROLES";

    /// <summary>Values of the legacy document classification.</summary>
    public static readonly IReadOnlyList<string> All = [Internal, Customer];

    /// <summary>Values allowed in <c>chat_message.retrieval_audience</c>.</summary>
    public static readonly IReadOnlyList<string> RetrievalScopes = [Internal, Customer, Shared];

    /// <summary>
    /// The tag of the interim (never deployed) classification. It grants nothing: the column is authoritative. Documents still carrying it are
    /// flagged to Admins for review (<c>legacyAudienceTag</c>), never reclassified automatically.
    /// </summary>
    public const string LegacyCustomerTag = "audience:customer";
}

/// <summary>
/// Knowledge chat. The API (not the RAG service) decides what may be read. Shared publication scope (2026-11-23): every signed-in role
/// (Customer, Technician, Sales, Manager, Admin) retrieves exactly the same set — the published version of each PUBLISHED document that an Admin
/// approved for all signed-in roles — passed as the retrieval allow-list; the citations are re-checked against the same set after generation,
/// so a version unpublished or superseded meanwhile is never output or cited.
/// Cost is bounded by a transactional per-user/global token ledger with leased reservations and a per-user
/// concurrent-generation cap.
/// Retrieval and generation values (top-k, context and output budgets, min score, model, temperature, history length, prompt
/// version) come from the effective admin configuration, read per request; the quota estimate follows the same values.
/// The model only produces text; it can never change contracts, payments or any data.
/// </summary>
/// <para>
/// Configuration advice (2026-11-17): questions about what to install (roof m², budget, kWh/month, "bao nhiêu tấm / kWp", which inverter) are
/// answered by the deterministic <see cref="Advisor.SolarAdvisorService"/> from ACTIVE catalog data, the caller's own site / simulation and
/// PVGIS — never by the model, which is not called for them (no Ollama cost). Missing essentials are asked for; values given in earlier user
/// turns of the session are reused. The advisor only reads; it never creates a quotation, contract, payment or appointment.
/// </para>
public sealed class ChatService(IKnowledgeUnitOfWork unitOfWork, IRagServiceClient rag, RagOptions options, IRagConfigurationProvider configuration, TimeProvider time,
    ILogger<ChatService> logger, Advisor.SolarAdvisorService? advisor = null)
{
    /// <summary>Without dependency injection: the effective configuration is read from the same unit of work, uncached.</summary>
    public ChatService(IKnowledgeUnitOfWork unitOfWork, IRagServiceClient rag, RagOptions options, TimeProvider time, ILogger<ChatService> logger,
        Advisor.SolarAdvisorService? advisor = null)
        : this(unitOfWork, rag, options, EffectiveRagConfigurationProvider.Uncached(unitOfWork, options, time), time, logger, advisor)
    {
    }

    public const string InsufficientAnswer =
        "Xin lỗi, tôi chưa có đủ tài liệu đã được duyệt để trả lời chính xác câu hỏi này. Vui lòng liên hệ bộ phận tư vấn của SmartSolar.";
    /// <summary>Shown to a customer instead of an answer they may not read (the stored text is not deleted).</summary>
    public const string WithheldAnswer =
        "Câu trả lời này đã được tạm ẩn trong khi SmartSolar rà soát lại nguồn tài liệu. Vui lòng đặt lại câu hỏi để nhận câu trả lời mới.";
    public const string Notice = "Câu trả lời được tạo tự động từ tài liệu đã công bố và có thể chưa đầy đủ; không thay thế tư vấn kỹ thuật hoặc điều khoản hợp đồng.";

    public async Task<OperationResult<ChatAnswerView>> AskAsync(Guid userId, Guid? sessionId, string question, string? clientMessageId, CancellationToken cancellationToken,
        KnowledgeAudience audience = KnowledgeAudience.Customer, Advisor.SolarAdvisorInput? advisorInput = null, string? userRole = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var config = await configuration.GetAsync(cancellationToken);
        var c = config.Values;
        // The admin limit, never above the deployment's own ceiling (Rag:MaxQuestionCharacters).
        var maxQuestion = Math.Min(options.MaxQuestionCharacters, c.MaxQuestionCharacters);
        question = question?.Trim() ?? "";
        if (question.Length == 0 || question.Length > maxQuestion)
            return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.MessageTooLong, $"Questions must be 1–{maxQuestion} characters.", new { maxCharacters = maxQuestion });
        if (clientMessageId is { Length: > 64 }) return OperationResult<ChatAnswerView>.Fail(CommonErrorCodes.ValidationFailed, "clientMessageId must be at most 64 characters.");

        ChatSession? session = null;
        if (sessionId is null && clientMessageId is not null
            && await unitOfWork.FindUserMessageByClientIdAsync(userId, clientMessageId, cancellationToken) is { } retriedFirst
            && await unitOfWork.FindReplyAsync(retriedFirst.Id, cancellationToken) is { } firstReply)
        {
            // A retried FIRST question (the client did not receive the new session id): the stored exchange, not a second session and a
            // second generation.
            return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(retriedFirst.SessionId, retriedFirst, firstReply, cancellationToken,
                await VisibleAsync(cancellationToken), audience));
        }
        if (sessionId is { } id)
        {
            session = await unitOfWork.FindSessionAsync(id, cancellationToken);
            if (session is null || session.UserId != userId) return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.SessionNotFound, "Chat session was not found.");
            // A retried request (same client message id) returns the stored exchange instead of paying twice.
            if (clientMessageId is not null && await unitOfWork.FindMessageByClientIdAsync(id, clientMessageId, cancellationToken) is { } existing
                && await unitOfWork.FindReplyAsync(existing.Id, cancellationToken) is { } reply)
                return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(id, existing, reply, cancellationToken, await VisibleAsync(cancellationToken), audience));
        }

        // ---- route before admission (reads only): the deterministic advisor never needs a paid call
        List<RagHistoryTurn> history = [];
        if (session is not null)
        {
            // The same rule as the session view: an answer the customer may not read is never fed back to the model either.
            var previous = await unitOfWork.GetMessagesAsync(session.Id, c.MaxHistoryMessages, cancellationToken);
            var previousCitations = await unitOfWork.GetCitationsAsync(previous.Select(m => m.Id).ToList(), cancellationToken);
            var visible = await VisibleAsync(cancellationToken);
            history = previous.Where(m => m.Status != ChatMessageStatuses.Failed && !View(m, previousCitations, visible, audience).Withheld)
                .Select(m => new RagHistoryTurn(m.Role, m.Content)).ToList();
        }
        Advisor.SolarAdvisorInput? advice = null;
        if (advisor is not null)
        {
            var extracted = Advisor.SolarAdvisorTextExtractor.Extract(question);
            Advisor.SolarAdvisorInput? earlier = null;
            foreach (var turn in history.Where(h => h.Role == ChatRoles.User))
                earlier = Advisor.SolarAdvisorTextExtractor.Extract(turn.Content).Over(earlier);
            var followUp = (extracted.TiltDegree is not null || extracted.AzimuthDegree is not null) && earlier?.HasSizingInput == true;
            if (advisorInput is not null || Advisor.SolarAdvisorTextExtractor.IsAdvisorQuestion(question, extracted) || followUp)
                advice = (advisorInput ?? new Advisor.SolarAdvisorInput()).Over(extracted.Over(earlier));
        }
        var allowed = advice is null ? await AllowedAsync(cancellationToken) : [];
        // A paid call happens only on the knowledge path with something the caller may read.
        var billable = advice is null && allowed.Count > 0;
        if (billable && !c.GenerationEnabled)
            return OperationResult<ChatAnswerView>.Fail(AiCostErrorCodes.GenerationDisabled,
                "The AI assistant is switched off by the administrator. Product and configuration advice remain available.");

        // ---- one atomic admission: quotas, concurrency, idempotency and the USD budget, before any provider call
        var now = time.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        // Only an ACTIVE configuration with a usable registry model names the model; otherwise the RAG service's environment default runs
        // and is estimated at the most expensive usable billed price.
        var sendModel = !config.Bootstrap && config.Model is not null;
        var generationPrice = GenerationPrice(sendModel ? config.Model : null);
        var embeddingPrice = EmbeddingPrice(options.EmbeddingModel);
        var reservedUsd = billable ? AiCostCalculator.MaxChat(generationPrice, embeddingPrice, question.Length, c.MaxContextTokens, c.MaxOutputTokens) : 0m;
        var reservedTokens = billable ? (long)c.MaxContextTokens + c.MaxOutputTokens + question.Length / 3 + 500 : 1;
        var timeoutSeconds = Math.Clamp(Math.Min(c.GenerationTimeoutSeconds, options.RequestTimeoutSeconds), 5, 120);
        // The lease outlives the longest possible call, so only a crashed or abandoned request lets it expire.
        var lease = TimeSpan.FromSeconds(timeoutSeconds + 60);
        AiAdmission admission;
        try
        {
            admission = await unitOfWork.TryAdmitAsync(new AiAdmissionRequest(userId, userRole, date, reservedTokens, reservedUsd, billable, clientMessageId,
                config.Version, c.UserRequestsPerDay, c.UserConcurrency, c.GlobalConcurrency, c.DailyBudgetUsd, c.MonthlyBudgetUsd, now, now + lease),
                options, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            // The same client message is being answered right now (double submit, retry, another replica): never a second paid call.
            return OperationResult<ChatAnswerView>.Fail(AiCostErrorCodes.RequestInProgress,
                "This question is already being answered. Retry with the same clientMessageId in a few seconds to get the answer.", new { retryAfterSeconds = 3 });
        }
        if (!admission.Admitted) return Refused(admission);
        var prepareMs = clock.ElapsedMilliseconds;

        long input = 0, output = 0;
        var settlement = Settlement(AiUsageOutcomes.Expired, uncertain: true, null, null, now);   // replaced below; kept if this method throws
        try
        {
            if (advice is not null)
            {
                Advisor.SolarAdvisorView view;
                try
                {
                    view = await advisor!.AdviseAsync(userId, advice, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Solar advisor failed (the knowledge chat is unaffected).");
                    settlement = Settlement(AiUsageOutcomes.ProviderError, false, null, null, time.GetUtcNow());
                    return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.AdvisorUnavailable,
                        "The configuration advisor is temporarily unavailable. Please try again later; other questions still work.");
                }
                var stored = await PersistAsync(userId, session, question, clientMessageId, Advisor.SolarAdvisorRenderer.Render(view), true, [],
                    new Dictionary<Guid, RagAllowedVersion>(), new RagAnswerResult(true, null, null, true, [], 0, 0, "solar-advisor", 0, 0), audience, cancellationToken, null);
                settlement = Settlement(AiUsageOutcomes.Answered, false, null, stored.Value?.Answer.MessageId, time.GetUtcNow());
                logger.LogInformation("Chat advisor answer: status={Status} advisor={AdvisorMs}ms (provider={ProviderMs}ms) total={TotalMs}ms; no model call.",
                    view.Status, clock.ElapsedMilliseconds - prepareMs, view.ProviderMs, clock.ElapsedMilliseconds);
                return stored.Succeeded ? OperationResult<ChatAnswerView>.Ok(stored.Value! with { Advisor = view }) : stored;
            }

            RagAnswerResult result;
            if (allowed.Count == 0)
            {
                result = new RagAnswerResult(true, null, null, false, [], 0, 0, null, 0, 0, InsufficientReason: InsufficientReasons.NoPublishedDocuments);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    // Bootstrap with no usable model, or an ACTIVE model that left the registry (or lost its price): the RAG service's environment
                    // default is used and costed at the most expensive usable price (see GenerationPrice) until an Admin activates a usable model.
                    var send = sendModel;
                    if (!config.Bootstrap && !config.ModelRegistered)
                        logger.LogWarning("Active RAG configuration {Version} names generation model {Model}, which is not a usable registry model; the RAG service default model is used.",
                            config.Version, c.GenerationModel);
                    result = await rag.AnswerAsync(new RagAnswerRequest(question, history, allowed, Math.Clamp(c.TopK, 1, Math.Max(1, options.MaxTopK)),
                        c.MaxContextTokens, c.MaxOutputTokens, config.Version, send ? c.GenerationModel : null, send ? c.Temperature : null,
                        send ? config.TokenParameter : null, c.MinScore, c.PromptVersion, send ? c.ReasoningEffort : null, timeoutSeconds),
                        timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new RagAnswerResult(false, "timeout", null, false, [], 0, 0, null, 0, 0, Billing: "uncertain");
                }
            }
            var ragMs = clock.ElapsedMilliseconds - prepareMs;
            if (!result.Reached)
            {
                logger.LogWarning("RAG answer unavailable: {Error} (billing {Billing}, prepare={PrepareMs}ms, rag={RagMs}ms).", result.Error, result.Billing, prepareMs, ragMs);
                var outcome = result.Error switch
                {
                    "timeout" => AiUsageOutcomes.Timeout,
                    "busy" => AiUsageOutcomes.Busy,
                    "provider rate limited" => AiUsageOutcomes.ProviderRateLimited,
                    _ => AiUsageOutcomes.ProviderError,
                };
                settlement = Costed(result, outcome, uncertain: result.Billing == "uncertain", null, config, (int)clock.ElapsedMilliseconds);
                return result.Error switch
                {
                    "timeout" => OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.Timeout,
                        "The assistant took too long to answer. Please try again; a shorter question is usually faster."),
                    "busy" => OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.Busy, "The assistant is answering other questions. Please try again in a moment."),
                    _ => OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.ServiceUnavailable, "The assistant is temporarily unavailable. Please try again later."),
                };
            }
            input = result.PromptTokens;
            output = result.CompletionTokens;

            // Re-check after generation: a version unpublished during the call must not be output or cited.
            var stillAllowed = (await AllowedAsync(cancellationToken)).ToDictionary(v => v.VersionId);
            var citations = result.Citations.Where(c => stillAllowed.ContainsKey(c.VersionId)).ToList();
            var grounded = result.Grounded && !string.IsNullOrWhiteSpace(result.Answer) && citations.Count > 0 && citations.Count == result.Citations.Count;
            var answerText = grounded ? result.Answer!.Trim() : InsufficientAnswer;
            var insufficientReason = grounded ? null
                : result.Citations.Count > 0 && citations.Count < result.Citations.Count ? InsufficientReasons.CitationsRevoked
                : result.InsufficientReason ?? (result.Grounded ? InsufficientReasons.ModelInsufficient : InsufficientReasons.Unspecified);
            // Safe structured diagnostics: no question text, no document content, no secrets.
            logger.LogInformation("Chat knowledge outcome: grounded={Grounded} reason={Reason} role={Role} allowedVersions={Allowed} retrievedChunks={Retrieved} usableChunks={Usable} citations={Citations}/{Proposed}.",
                grounded, insufficientReason ?? "-", userRole ?? "-", allowed.Count, result.RetrievedChunks, result.UsableChunks, citations.Count, result.Citations.Count);

            var persistStarted = clock.ElapsedMilliseconds;
            var persisted = await PersistAsync(userId, session, question, clientMessageId, answerText, grounded, grounded ? citations : [], stillAllowed, result, audience, cancellationToken,
                insufficientReason);
            settlement = Costed(result, grounded ? AiUsageOutcomes.Answered : AiUsageOutcomes.InsufficientContext, uncertain: false, persisted.Value?.Answer.MessageId,
                config, (int)clock.ElapsedMilliseconds);
            // Where the time went (no question text): API preparation (configuration, session, history, routing, admission), the RAG call
            // (its retrieval, context construction and generation), and storing the exchange.
            logger.LogInformation("Chat answer timings: prepare={PrepareMs}ms rag={RagMs}ms (retrieval={RetrievalMs}ms context={ContextMs}ms generation={GenerationMs}ms) persist={PersistMs}ms total={TotalMs}ms grounded={Grounded} costUsd={CostUsd} configVersion={Version}.",
                prepareMs, ragMs, result.RetrievalMs, result.ContextMs, result.GenerationMs, clock.ElapsedMilliseconds - persistStarted, clock.ElapsedMilliseconds, grounded,
                settlement.CostUsd, config.Version);
            return persisted;
        }
        finally
        {
            // Actual provider usage replaces the estimate; the USD reservation is settled exactly once (uncertain charges stay counted).
            await unitOfWork.SettleChatAsync(admission.ReservationId!.Value, input, output, settlement with { LatencyMs = (int)clock.ElapsedMilliseconds },
                CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ cost accounting

    private static OperationResult<ChatAnswerView> Refused(AiAdmission admission)
    {
        var retry = new { retryAfterSeconds = (int)Math.Ceiling((admission.RetryAfter ?? TimeSpan.FromSeconds(5)).TotalSeconds) };
        return admission.Limit switch
        {
            // The previous codes are kept for the per-user limits (clients already handle them).
            AiCostErrorCodes.Concurrency => OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.TooManyConcurrent, "Please wait for your previous question to be answered.", retry),
            AiCostErrorCodes.UserQuotaExceeded or AiCostErrorCodes.GlobalTokenQuota => OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.QuotaExceeded,
                "The daily chat allowance has been reached. Please try again tomorrow.", retry),
            AiCostErrorCodes.RequestInProgress => OperationResult<ChatAnswerView>.Fail(AiCostErrorCodes.RequestInProgress,
                "This question is already being answered. Retry with the same clientMessageId in a few seconds to get the answer.", retry),
            AiCostErrorCodes.GlobalBusy => OperationResult<ChatAnswerView>.Fail(AiCostErrorCodes.GlobalBusy,
                "The assistant is answering many questions right now. Please try again in a few seconds.", retry),
            _ => OperationResult<ChatAnswerView>.Fail(AiCostErrorCodes.BudgetExceeded,
                "The AI assistant has reached its spending limit for now. Product and configuration advice remain available.", retry),
        };
    }

    /// <summary>
    /// Price of the generation model for the estimate: the requested registry model; without one (the RAG service default is used) the most
    /// expensive usable billed model, so the reservation is never too small.
    /// </summary>
    private AiPrice GenerationPrice(RagGenerationModelOptions? model)
    {
        var priced = model ?? Rules.Models.Where(m => m.Usable && m.Billed).OrderByDescending(m => m.UnitCost).FirstOrDefault();
        return priced is { Billed: true }
            ? new AiPrice(priced.InputUsdPerMillion ?? 0, priced.CachedInputUsdPerMillion ?? priced.InputUsdPerMillion ?? 0, priced.OutputUsdPerMillion ?? 0)
            : AiPrice.Free;
    }

    /// <summary>Embedding price of a model; an unknown model is assumed billed at the highest known embedding price.</summary>
    private decimal EmbeddingPrice(string? model)
        => Rules.FindEmbeddingModel(model) is { } known ? (known.Provider == RagProviders.OpenAi ? known.InputUsdPerMillion ?? 0 : 0)
            : Rules.EmbeddingModels.Where(m => m.Provider == RagProviders.OpenAi).Select(m => m.InputUsdPerMillion ?? 0).DefaultIfEmpty(0).Max();

    private RagConfigurationRules Rules => _rules ??= new RagConfigurationRules(options);
    private RagConfigurationRules? _rules;

    private static AiChatSettlement Settlement(string outcome, bool uncertain, Guid? messageId, Guid? answerMessageId, DateTimeOffset now)
        => new(outcome, uncertain, answerMessageId ?? messageId, null, null, 0, 0, null, null, 0, 0, 0, 0, 0, true, 0, now);

    /// <summary>The settlement of a RAG call from what the service reported; only billed providers cost money.</summary>
    private AiChatSettlement Costed(RagAnswerResult r, string outcome, bool uncertain, Guid? messageId, EffectiveRagConfiguration config, int latencyMs)
    {
        var embeddingModel = r.EmbeddingModel ?? options.EmbeddingModel;
        var embeddingBilled = r.EmbeddingProvider is null ? r.EmbeddingTokens > 0 : r.EmbeddingProvider == RagProviders.OpenAi;
        var embeddingCost = embeddingBilled ? AiCostCalculator.Embedding(EmbeddingPrice(embeddingModel), r.EmbeddingTokens) : 0m;
        var generationBilled = r.GenerationProvider == RagProviders.OpenAi;
        var model = generationBilled ? Rules.FindModel(r.Model) ?? Rules.Models.FirstOrDefault(m => r.Model is not null && r.Model.StartsWith(m.Id, StringComparison.Ordinal)) : null;
        var generationCost = generationBilled
            ? AiCostCalculator.Generation(model is { Billed: true } ? GenerationPrice(model) : GenerationPrice(null), r.PromptTokens, r.CachedPromptTokens, r.CompletionTokens)
            : 0m;
        return new AiChatSettlement(outcome, uncertain, messageId, r.EmbeddingProvider, embeddingModel, r.EmbeddingTokens, embeddingCost,
            r.GenerationProvider, r.Model, r.PromptTokens, r.CachedPromptTokens, r.CompletionTokens, r.ReasoningTokens, generationCost, r.UsageReported, latencyMs,
            time.GetUtcNow());
    }

    public async Task<IReadOnlyList<ChatSessionView>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
        => (await unitOfWork.ListSessionsAsync(userId, cancellationToken)).Select(s => new ChatSessionView(s.Id, s.Title, s.CreatedAt, s.UpdatedAt)).ToList();

    /// <summary>
    /// A session with its messages (owner only). Stored citations (snippets) are shown only while their version is still retrievable; legacy
    /// answers follow <see cref="WithheldFor"/>.
    /// </summary>
    public async Task<OperationResult<ChatSessionDetailView>> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken,
        KnowledgeAudience audience = KnowledgeAudience.Customer)
    {
        var session = await unitOfWork.FindSessionAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId) return OperationResult<ChatSessionDetailView>.Fail(KnowledgeErrorCodes.SessionNotFound, "Chat session was not found.");
        var messages = await unitOfWork.GetMessagesAsync(sessionId, null, cancellationToken);
        var citations = await unitOfWork.GetCitationsAsync(messages.Select(m => m.Id).ToList(), cancellationToken);
        var visible = await VisibleAsync(cancellationToken);
        return OperationResult<ChatSessionDetailView>.Ok(new ChatSessionDetailView(new ChatSessionView(session.Id, session.Title, session.CreatedAt, session.UpdatedAt),
            messages.Select(m => View(m, citations, visible, audience)).ToList()));
    }

    private async Task<OperationResult<ChatAnswerView>> PersistAsync(Guid userId, ChatSession? session, string question, string? clientMessageId, string answer,
        bool grounded, IReadOnlyList<RagCitation> citations, IReadOnlyDictionary<Guid, RagAllowedVersion> versions, RagAnswerResult result, KnowledgeAudience audience,
        CancellationToken cancellationToken, string? insufficientReason)
    {
        var now = time.GetUtcNow();
        var isNew = session is null;
        session ??= new ChatSession { Id = Guid.NewGuid(), UserId = userId, Title = question.Length <= 80 ? question : question[..80] + "…", CreatedAt = now, UpdatedAt = now };
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.User, Content = question, Status = ChatMessageStatuses.Completed,
            ClientMessageId = clientMessageId, CreatedAt = now,
        };
        var assistant = new ChatMessage
        {
            Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.Assistant, Content = answer,
            Status = grounded ? ChatMessageStatuses.Completed : ChatMessageStatuses.InsufficientContext, ReplyToMessageId = userMessage.Id,
            PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens, Model = result.Model,
            LatencyMs = result.RetrievalMs + result.GenerationMs, CreatedAt = now.AddTicks(1),
            // Every new answer is retrieved from the shared scope, whoever asked.
            RetrievalAudience = KnowledgeAudiences.Shared,
        };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                if (isNew) unitOfWork.Add(session);
                unitOfWork.Add(userMessage);
                unitOfWork.Add(assistant);
                var rank = 0;
                foreach (var c in citations)
                {
                    var v = versions[c.VersionId];
                    unitOfWork.Add(new ChatCitation
                    {
                        Id = Guid.NewGuid(), MessageId = assistant.Id, Rank = ++rank, DocumentId = v.DocumentId, VersionId = c.VersionId,
                        ChunkId = c.ChunkId.Length <= 100 ? c.ChunkId : c.ChunkId[..100], DocumentTitle = v.Title, VersionNumber = v.VersionNumber,
                        PageNumber = c.Page, Section = c.Section is { Length: > 200 } s ? s[..200] : c.Section, Score = c.Score,
                        Snippet = c.Snippet is { Length: > 500 } sn ? sn[..500] : c.Snippet,
                    });
                }
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException) when (clientMessageId is not null)
        {
            // The same client message raced in twice; answer with the stored one.
            var existing = await unitOfWork.FindMessageByClientIdAsync(session.Id, clientMessageId, cancellationToken);
            var reply = existing is null ? null : await unitOfWork.FindReplyAsync(existing.Id, cancellationToken);
            if (existing is not null && reply is not null)
                return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(session.Id, existing, reply, cancellationToken, await VisibleAsync(cancellationToken), audience));
            throw;
        }
        return OperationResult<ChatAnswerView>.Ok((await AnswerViewAsync(session.Id, userMessage, assistant, cancellationToken, await VisibleAsync(cancellationToken), audience))
            with { InsufficientReason = insufficientReason });
    }

    /// <summary>The one retrieval allow-list for every signed-in role (published + approved for all signed-in roles).</summary>
    private Task<IReadOnlyList<RagAllowedVersion>> AllowedAsync(CancellationToken cancellationToken) => unitOfWork.GetRetrievableVersionsAsync(cancellationToken);

    /// <summary>What stored citations may still show: the currently retrievable versions (and their documents, for the legacy rule).</summary>
    private async Task<Retrievable> VisibleAsync(CancellationToken cancellationToken)
    {
        var allowed = await AllowedAsync(cancellationToken);
        return new Retrievable(allowed.Select(v => v.VersionId).ToHashSet(), allowed.Select(v => v.DocumentId).ToHashSet());
    }

    private sealed record Retrievable(IReadOnlySet<Guid> Versions, IReadOnlySet<Guid> Documents);

    private async Task<ChatAnswerView> AnswerViewAsync(Guid sessionId, ChatMessage question, ChatMessage answer, CancellationToken cancellationToken, Retrievable visible,
        KnowledgeAudience audience)
    {
        var citations = await unitOfWork.GetCitationsAsync([answer.Id], cancellationToken);
        var view = View(answer, citations, visible, audience);
        return new ChatAnswerView(sessionId, View(question, citations, visible, audience), view, answer.Status == ChatMessageStatuses.Completed && !view.Withheld, Notice);
    }

    /// <summary>
    /// Migration-compatible rule for LEGACY answers (stored before the shared scope): a customer never reads an answer that was not retrieved
    /// for the customer audience — answers from before audiences existed (null) may have used any published document, and answers retrieved
    /// for staff (INTERNAL) may use internal ones. Their text cannot be redacted safely, so it is withheld (kept in the database for review)
    /// and never fed back to the model. A legacy CUSTOMER answer is withheld from a customer when it cites a document that is no longer
    /// retrievable. Answers retrieved from the shared scope are never withheld from their owner (every role could read their sources).
    /// </summary>
    private static bool WithheldFor(KnowledgeAudience audience, ChatMessage m, IReadOnlyList<ChatCitation> own, Retrievable visible)
        => audience == KnowledgeAudience.Customer && m.Role == ChatRoles.Assistant && m.RetrievalAudience != KnowledgeAudiences.Shared
           && (m.RetrievalAudience != KnowledgeAudiences.Customer || own.Any(c => !visible.Documents.Contains(c.DocumentId)));

    /// <summary>Citations are shown only for versions still retrievable (published and approved): unpublished or superseded sources disappear.</summary>
    private static ChatMessageView View(ChatMessage m, IReadOnlyList<ChatCitation> citations, Retrievable visible, KnowledgeAudience audience)
    {
        var own = citations.Where(c => c.MessageId == m.Id).ToList();
        return WithheldFor(audience, m, own, visible)
            ? new(m.Id, m.Role, WithheldAnswer, m.Status, m.CreatedAt, [], Withheld: true)
            : new(m.Id, m.Role, m.Content, m.Status, m.CreatedAt,
                own.Where(c => visible.Versions.Contains(c.VersionId)).OrderBy(c => c.Rank)
                    .Select(c => new CitationView(c.Rank, c.DocumentId, c.DocumentTitle, c.VersionId, c.VersionNumber, c.ChunkId, c.PageNumber, c.Section, c.Score, c.Snippet)).ToList());
    }
}
