using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.KnowledgeBase;

public sealed record CitationView(int Rank, Guid DocumentId, string DocumentTitle, Guid VersionId, int VersionNumber, string ChunkId, int? Page, string? Section,
    double Score, string? Snippet);

public sealed record ChatMessageView(Guid MessageId, string Role, string Content, string Status, DateTimeOffset CreatedAt, IReadOnlyList<CitationView> Citations);

public sealed record ChatAnswerView(Guid SessionId, ChatMessageView Question, ChatMessageView Answer, bool Grounded, string Notice);

public sealed record ChatSessionView(Guid SessionId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatSessionDetailView(ChatSessionView Session, IReadOnlyList<ChatMessageView> Messages);

/// <summary>
/// Customer-facing knowledge chat. The API (not the RAG service) decides what may be read: only the
/// published version of each published global document is passed as the retrieval filter, and the
/// citations are re-checked after generation so a document unpublished meanwhile never appears.
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
    public const string Notice = "Câu trả lời được tạo tự động từ tài liệu đã công bố và có thể chưa đầy đủ; không thay thế tư vấn kỹ thuật hoặc điều khoản hợp đồng.";

    public async Task<OperationResult<ChatAnswerView>> AskAsync(Guid userId, Guid? sessionId, string question, string? clientMessageId, CancellationToken cancellationToken)
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
                return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(id, existing, reply, cancellationToken));
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
            var history = session is null
                ? []
                : (await unitOfWork.GetMessagesAsync(session.Id, c.MaxHistoryMessages, cancellationToken))
                    .Where(m => m.Status != ChatMessageStatuses.Failed).Select(m => new RagHistoryTurn(m.Role, m.Content)).ToList();
            var allowed = await unitOfWork.GetRetrievableVersionsAsync(cancellationToken);

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
                    // environment defaults (RAG_LLM_MODEL / RAG_LLM_TEMPERATURE); only an ACTIVE configuration overrides them.
                    var bootstrap = config.Bootstrap;
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
            var stillAllowed = (await unitOfWork.GetRetrievableVersionsAsync(cancellationToken)).ToDictionary(v => v.VersionId);
            var citations = result.Citations.Where(c => stillAllowed.ContainsKey(c.VersionId)).ToList();
            var grounded = result.Grounded && !string.IsNullOrWhiteSpace(result.Answer) && citations.Count > 0 && citations.Count == result.Citations.Count;
            var answerText = grounded ? result.Answer!.Trim() : InsufficientAnswer;

            return await PersistAsync(userId, session, question, clientMessageId, answerText, grounded, grounded ? citations : [], stillAllowed, result, cancellationToken);
        }
        finally
        {
            // Actual provider usage replaces the estimate (zero when the model was never called).
            await unitOfWork.CompleteUsageAsync(reservation.ReservationId!.Value, input, output, time.GetUtcNow(), CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<ChatSessionView>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
        => (await unitOfWork.ListSessionsAsync(userId, cancellationToken)).Select(s => new ChatSessionView(s.Id, s.Title, s.CreatedAt, s.UpdatedAt)).ToList();

    public async Task<OperationResult<ChatSessionDetailView>> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await unitOfWork.FindSessionAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId) return OperationResult<ChatSessionDetailView>.Fail(KnowledgeErrorCodes.SessionNotFound, "Chat session was not found.");
        var messages = await unitOfWork.GetMessagesAsync(sessionId, null, cancellationToken);
        var citations = await unitOfWork.GetCitationsAsync(messages.Select(m => m.Id).ToList(), cancellationToken);
        return OperationResult<ChatSessionDetailView>.Ok(new ChatSessionDetailView(new ChatSessionView(session.Id, session.Title, session.CreatedAt, session.UpdatedAt),
            messages.Select(m => View(m, citations)).ToList()));
    }

    private async Task<OperationResult<ChatAnswerView>> PersistAsync(Guid userId, ChatSession? session, string question, string? clientMessageId, string answer,
        bool grounded, IReadOnlyList<RagCitation> citations, IReadOnlyDictionary<Guid, RagAllowedVersion> versions, RagAnswerResult result, CancellationToken cancellationToken)
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
            if (existing is not null && reply is not null) return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(session.Id, existing, reply, cancellationToken));
            throw;
        }
        return OperationResult<ChatAnswerView>.Ok(await AnswerViewAsync(session.Id, userMessage, assistant, cancellationToken));
    }

    private async Task<ChatAnswerView> AnswerViewAsync(Guid sessionId, ChatMessage question, ChatMessage answer, CancellationToken cancellationToken)
    {
        var citations = await unitOfWork.GetCitationsAsync([answer.Id], cancellationToken);
        return new ChatAnswerView(sessionId, View(question, citations), View(answer, citations), answer.Status == ChatMessageStatuses.Completed, Notice);
    }

    private static ChatMessageView View(ChatMessage m, IReadOnlyList<ChatCitation> citations) => new(m.Id, m.Role, m.Content, m.Status, m.CreatedAt,
        citations.Where(c => c.MessageId == m.Id).OrderBy(c => c.Rank)
            .Select(c => new CitationView(c.Rank, c.DocumentId, c.DocumentTitle, c.VersionId, c.VersionNumber, c.ChunkId, c.PageNumber, c.Section, c.Score, c.Snippet)).ToList());
}
