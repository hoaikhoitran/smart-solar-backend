using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed record CitationView(int Rank, Guid DocumentId, string DocumentTitle, Guid VersionId, int VersionNumber, string ChunkId, int? Page, string? Section,
    double Score, string? Snippet);

/// <summary>
/// <see cref="Withheld"/>: an assistant answer whose text a customer may not read (it was generated before customer-only retrieval
/// existed, or for staff), shown as <see cref="ChatService.WithheldAnswer"/> without citations. The stored text is kept for review.
/// </summary>
public sealed record ChatMessageView(Guid MessageId, string Role, string Content, string Status, DateTimeOffset CreatedAt, IReadOnlyList<CitationView> Citations,
    bool Withheld = false);

public sealed record ChatAnswerView(Guid SessionId, ChatMessageView Question, ChatMessageView Answer, bool Grounded, string Notice);

public sealed record ChatSessionView(Guid SessionId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatSessionDetailView(ChatSessionView Session, IReadOnlyList<ChatMessageView> Messages);

/// <summary>Who the knowledge is retrieved for. <see cref="Customer"/> is the restricted default.</summary>
public enum KnowledgeAudience
{
    /// <summary>Only documents classified as customer-visible (unclassified documents are internal).</summary>
    Customer,
    /// <summary>Signed-in staff: every published document.</summary>
    Staff,
}

/// <summary>Persisted audience of a knowledge document (<c>knowledge_document.audience</c>) and of a generated answer.</summary>
public static class KnowledgeAudiences
{
    /// <summary>Staff only. The default, and the value of every document that existed before the column.</summary>
    public const string Internal = "INTERNAL";
    /// <summary>Customers (and staff) may retrieve it.</summary>
    public const string Customer = "CUSTOMER";

    public static readonly IReadOnlyList<string> All = [Internal, Customer];

    /// <summary>
    /// The tag of the interim (never deployed) classification. It grants nothing: the column is authoritative. Documents still carrying it are
    /// flagged to Admins for review (<c>legacyAudienceTag</c>), never reclassified automatically.
    /// </summary>
    public const string LegacyCustomerTag = "audience:customer";
}

/// <summary>
/// Knowledge chat. The API (not the RAG service) decides what may be read: only the published version of each published document
/// the caller's audience may read (a Customer: customer-visible documents only) is passed as the retrieval filter, and the citations are
/// re-checked against the same audience after generation so a document unpublished or reclassified meanwhile never appears.
/// Cost is bounded by a transactional per-user/global token ledger with leased reservations and a per-user
/// concurrent-generation cap.
/// Retrieval and generation values (top-k, context and output budgets, min score, model, temperature, history length, prompt
/// version) come from the effective admin configuration, read per request; the quota estimate follows the same values.
/// The model only produces text; it can never change contracts, payments or any data.
/// </summary>
public sealed class ChatService(IKnowledgeUnitOfWork unitOfWork, IRagServiceClient rag, RagOptions options, IRagConfigurationProvider configuration, TimeProvider time,
    ILogger<ChatService> logger)
{
    /// <summary>Without dependency injection: the effective configuration is read from the same unit of work, uncached.</summary>
    public ChatService(IKnowledgeUnitOfWork unitOfWork, IRagServiceClient rag, RagOptions options, TimeProvider time, ILogger<ChatService> logger)
        : this(unitOfWork, rag, options, EffectiveRagConfigurationProvider.Uncached(unitOfWork, options, time), time, logger)
    {
    }

    public const string InsufficientAnswer =
        "Xin lỗi, tôi chưa có đủ tài liệu đã được duyệt để trả lời chính xác câu hỏi này. Vui lòng liên hệ bộ phận tư vấn của SmartSolar.";
    /// <summary>Shown to a customer instead of an answer they may not read (the stored text is not deleted).</summary>
    public const string WithheldAnswer =
        "Câu trả lời này đã được tạm ẩn trong khi SmartSolar rà soát lại nguồn tài liệu. Vui lòng đặt lại câu hỏi để nhận câu trả lời mới.";
    public const string Notice = "Câu trả lời được tạo tự động từ tài liệu đã công bố và có thể chưa đầy đủ; không thay thế tư vấn kỹ thuật hoặc điều khoản hợp đồng.";

    public async Task<OperationResult<ChatAnswerView>> AskAsync(Guid userId, Guid? sessionId, string question, string? clientMessageId, CancellationToken cancellationToken,
        KnowledgeAudience audience = KnowledgeAudience.Customer)
    {
        question = question?.Trim() ?? "";
        if (question.Length == 0 || question.Length > options.MaxQuestionCharacters)
            return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.MessageTooLong, $"Questions must be 1–{options.MaxQuestionCharacters} characters.");
        if (clientMessageId is { Length: > 64 }) return OperationResult<ChatAnswerView>.Fail(CommonErrorCodes.ValidationFailed, "clientMessageId must be at most 64 characters.");

        ChatSession? session = null;
        if (sessionId is { } id)
        {
            session = await unitOfWork.FindSessionAsync(id, cancellationToken);
            if (session is null || session.UserId != userId) return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.SessionNotFound, "Chat session was not found.");
            // A retried request (same client message id) returns the stored exchange instead of paying twice.
            if (clientMessageId is not null && await unitOfWork.FindMessageByClientIdAsync(id, clientMessageId, cancellationToken) is { } existing
                && await unitOfWork.FindReplyAsync(existing.Id, cancellationToken) is { } reply)
                return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(id, existing, reply, cancellationToken, await VisibleDocumentsAsync(audience, cancellationToken), audience));
        }

        var config = await configuration.GetAsync(cancellationToken);
        var c = config.Values;
        var now = time.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var reserved = (long)c.MaxContextTokens + c.MaxOutputTokens + question.Length / 3 + 500;
        // The lease outlives the longest possible call, so only a crashed or abandoned request lets it expire.
        var lease = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120) + 60);
        var reservation = await unitOfWork.TryReserveUsageAsync(userId, date, reserved, options, now, now + lease, cancellationToken);
        if (reservation.Limit == "CONCURRENCY") return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.TooManyConcurrent, "Please wait for your previous question to be answered.");
        if (!reservation.Reserved) return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.QuotaExceeded, "The daily chat allowance has been reached. Please try again tomorrow.");

        long input = 0, output = 0;
        try
        {
            List<RagHistoryTurn> history = [];
            if (session is not null)
            {
                // The same rule as the session view: an answer the customer may not read is never fed back to the model either.
                var previous = await unitOfWork.GetMessagesAsync(session.Id, c.MaxHistoryMessages, cancellationToken);
                var previousCitations = await unitOfWork.GetCitationsAsync(previous.Select(m => m.Id).ToList(), cancellationToken);
                var visible = await VisibleDocumentsAsync(audience, cancellationToken);
                history = previous.Where(m => m.Status != ChatMessageStatuses.Failed && !View(m, previousCitations, visible, audience).Withheld)
                    .Select(m => new RagHistoryTurn(m.Role, m.Content)).ToList();
            }
            var allowed = await AllowedAsync(audience, cancellationToken);

            RagAnswerResult result;
            if (allowed.Count == 0)
            {
                result = new RagAnswerResult(true, null, null, false, [], 0, 0, null, 0, 0);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120)));
                try
                {
                    // Bootstrap (no ACTIVE admin configuration): model, temperature and token parameter are left to the RAG service's own
                    // environment defaults (RAG_LLM_MODEL / RAG_LLM_TEMPERATURE); only an ACTIVE configuration overrides them. An ACTIVE
                    // model that is no longer in the registry is not sent either (the provider no longer serves it): the defaults apply
                    // until an Admin activates a configuration with a registered model.
                    var bootstrap = config.Bootstrap || !config.ModelRegistered;
                    if (!config.Bootstrap && !config.ModelRegistered)
                        logger.LogWarning("Active RAG configuration {Version} names generation model {Model}, which is not in Rag:GenerationModels; the RAG service default model is used.",
                            config.Version, c.GenerationModel);
                    result = await rag.AnswerAsync(new RagAnswerRequest(question, history, allowed, Math.Clamp(c.TopK, 1, Math.Max(1, options.MaxTopK)),
                        c.MaxContextTokens, c.MaxOutputTokens, config.Version, bootstrap ? null : c.GenerationModel, bootstrap ? null : c.Temperature,
                        bootstrap ? null : config.TokenParameter, c.MinScore, c.PromptVersion),
                        timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new RagAnswerResult(false, "timeout", null, false, [], 0, 0, null, 0, 0);
                }
            }
            if (!result.Reached)
            {
                logger.LogWarning("RAG answer unavailable: {Error}.", result.Error);
                return OperationResult<ChatAnswerView>.Fail(KnowledgeErrorCodes.ServiceUnavailable, "The assistant is temporarily unavailable. Please try again later.");
            }
            input = result.PromptTokens;
            output = result.CompletionTokens;

            // Re-check after generation: a version unpublished during the call must not be cited.
            var stillAllowed = (await AllowedAsync(audience, cancellationToken)).ToDictionary(v => v.VersionId);
            var citations = result.Citations.Where(c => stillAllowed.ContainsKey(c.VersionId)).ToList();
            var grounded = result.Grounded && !string.IsNullOrWhiteSpace(result.Answer) && citations.Count > 0 && citations.Count == result.Citations.Count;
            var answerText = grounded ? result.Answer!.Trim() : InsufficientAnswer;

            return await PersistAsync(userId, session, question, clientMessageId, answerText, grounded, grounded ? citations : [], stillAllowed, result, audience, cancellationToken);
        }
        finally
        {
            // Actual provider usage replaces the estimate (zero when the model was never called).
            await unitOfWork.CompleteUsageAsync(reservation.ReservationId!.Value, input, output, time.GetUtcNow(), CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<ChatSessionView>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
        => (await unitOfWork.ListSessionsAsync(userId, cancellationToken)).Select(s => new ChatSessionView(s.Id, s.Title, s.CreatedAt, s.UpdatedAt)).ToList();

    /// <summary>
    /// A session with its messages. For a Customer, stored citations (snippets) are shown only for documents still customer-visible, so a
    /// document reclassified as internal no longer surfaces through old citations.
    /// </summary>
    public async Task<OperationResult<ChatSessionDetailView>> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken,
        KnowledgeAudience audience = KnowledgeAudience.Customer)
    {
        var session = await unitOfWork.FindSessionAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId) return OperationResult<ChatSessionDetailView>.Fail(KnowledgeErrorCodes.SessionNotFound, "Chat session was not found.");
        var messages = await unitOfWork.GetMessagesAsync(sessionId, null, cancellationToken);
        var citations = await unitOfWork.GetCitationsAsync(messages.Select(m => m.Id).ToList(), cancellationToken);
        var visible = await VisibleDocumentsAsync(audience, cancellationToken);
        return OperationResult<ChatSessionDetailView>.Ok(new ChatSessionDetailView(new ChatSessionView(session.Id, session.Title, session.CreatedAt, session.UpdatedAt),
            messages.Select(m => View(m, citations, visible, audience)).ToList()));
    }

    private async Task<OperationResult<ChatAnswerView>> PersistAsync(Guid userId, ChatSession? session, string question, string? clientMessageId, string answer,
        bool grounded, IReadOnlyList<RagCitation> citations, IReadOnlyDictionary<Guid, RagAllowedVersion> versions, RagAnswerResult result, KnowledgeAudience audience,
        CancellationToken cancellationToken)
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
            RetrievalAudience = audience == KnowledgeAudience.Staff ? KnowledgeAudiences.Internal : KnowledgeAudiences.Customer,
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
                return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(session.Id, existing, reply, cancellationToken, versions.Values.Select(v => v.DocumentId).ToHashSet(), audience));
            throw;
        }
        return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(session.Id, userMessage, assistant, cancellationToken, null, audience));
    }

    private Task<IReadOnlyList<RagAllowedVersion>> AllowedAsync(KnowledgeAudience audience, CancellationToken cancellationToken)
        => audience == KnowledgeAudience.Staff ? unitOfWork.GetRetrievableVersionsAsync(cancellationToken) : unitOfWork.GetCustomerRetrievableVersionsAsync(cancellationToken);

    /// <summary>Documents whose stored citations the audience may still see (null = staff: every stored citation).</summary>
    private async Task<IReadOnlySet<Guid>?> VisibleDocumentsAsync(KnowledgeAudience audience, CancellationToken cancellationToken)
        => audience == KnowledgeAudience.Staff ? null : (await unitOfWork.GetCustomerRetrievableVersionsAsync(cancellationToken)).Select(v => v.DocumentId).ToHashSet();

    private async Task<ChatAnswerView> AnswerViewAsync(Guid sessionId, ChatMessage question, ChatMessage answer, CancellationToken cancellationToken, IReadOnlySet<Guid>? visibleDocuments,
        KnowledgeAudience audience)
    {
        var citations = await unitOfWork.GetCitationsAsync([answer.Id], cancellationToken);
        var view = View(answer, citations, visibleDocuments, audience);
        return new ChatAnswerView(sessionId, View(question, citations, visibleDocuments, audience), view, answer.Status == ChatMessageStatuses.Completed && !view.Withheld, Notice);
    }

    /// <summary>
    /// A customer never reads an answer that was not retrieved for the customer audience: answers from before audiences existed (null) may
    /// have used any published document, and answers retrieved for staff may use internal ones. Their text cannot be redacted safely, so it is
    /// withheld (kept in the database for review), and it is never fed back to the model as conversation history.
    /// </summary>
    private static bool WithheldFor(KnowledgeAudience audience, ChatMessage m)
        => audience == KnowledgeAudience.Customer && m.Role == ChatRoles.Assistant && m.RetrievalAudience != KnowledgeAudiences.Customer;

    /// <param name="visibleDocuments">Documents the reader may still see (null = staff: all). An answer citing any other document is withheld
    /// as a whole for the customer, because its text was written from that document (e.g. one reclassified as INTERNAL since).</param>
    private static ChatMessageView View(ChatMessage m, IReadOnlyList<ChatCitation> citations, IReadOnlySet<Guid>? visibleDocuments, KnowledgeAudience audience)
    {
        var own = citations.Where(c => c.MessageId == m.Id).ToList();
        var withheld = WithheldFor(audience, m) || (visibleDocuments is not null && own.Any(c => !visibleDocuments.Contains(c.DocumentId)));
        return withheld
            ? new(m.Id, m.Role, WithheldAnswer, m.Status, m.CreatedAt, [], Withheld: true)
            : new(m.Id, m.Role, m.Content, m.Status, m.CreatedAt,
                own.OrderBy(c => c.Rank)
                    .Select(c => new CitationView(c.Rank, c.DocumentId, c.DocumentTitle, c.VersionId, c.VersionNumber, c.ChunkId, c.PageNumber, c.Section, c.Score, c.Snippet)).ToList());
    }
}
