using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.KnowledgeBase;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Admin lifecycle of chatbot knowledge documents. The PDF/DOCX/TXT is uploaded by the frontend
/// to S3 (purpose KNOWLEDGE_DOCUMENT) and registered through <c>POST /api/files</c>; only verified
/// files are accepted. Only a PUBLISHED version is ever used to answer questions.
/// </summary>
[ApiController]
[Route("api/admin/knowledge-documents")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.KnowledgeBase)]
public sealed class AdminKnowledgeDocumentsController(KnowledgeDocumentHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? category, CancellationToken cancellationToken)
        => Respond(Modules.Common.Results.OperationResult<IReadOnlyList<KnowledgeDocumentView>>.Ok(await handlers.ListAsync(status, category, cancellationToken)));

    [HttpGet("{documentId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
        => Respond(await handlers.GetAsync(documentId, cancellationToken));

    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminRegisterPolicy)]
    public async Task<IActionResult> Register([FromBody] RegisterKnowledgeDocumentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin
            ? Respond(await handlers.RegisterAsync(admin, request.FileId, request.Title ?? "", request.Category ?? "", request.Tags, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpPost("{documentId:guid}/versions")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminRegisterPolicy)]
    public async Task<IActionResult> AddVersion(Guid documentId, [FromBody] AddKnowledgeVersionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await handlers.AddVersionAsync(admin, documentId, request.FileId, cancellationToken), StatusCodes.Status201Created) : UnauthorizedFailure();

    [HttpPut("{documentId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminRegisterPolicy)]
    public async Task<IActionResult> UpdateMetadata(Guid documentId, [FromBody] UpdateKnowledgeMetadataRequest request, CancellationToken cancellationToken)
        => Respond(await handlers.UpdateMetadataAsync(documentId, request.Revision, request.Title ?? "", request.Category ?? "", request.Tags, cancellationToken));

    [HttpPost("{documentId:guid}/publish")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminPublishPolicy)]
    public async Task<IActionResult> Publish(Guid documentId, [FromBody] PublishKnowledgeVersionRequest request, CancellationToken cancellationToken)
        => Respond(await handlers.PublishAsync(documentId, request.VersionId, request.Revision, cancellationToken));

    [HttpPost("{documentId:guid}/unpublish")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminPublishPolicy)]
    public async Task<IActionResult> Unpublish(Guid documentId, [FromBody] KnowledgeRevisionRequest request, CancellationToken cancellationToken)
        => Respond(await handlers.UnpublishAsync(documentId, request.Revision, cancellationToken));

    [HttpPost("{documentId:guid}/reindex")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReindexPolicy)]
    public async Task<IActionResult> Reindex(Guid documentId, [FromBody] ReindexKnowledgeVersionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await handlers.ReindexAsync(admin, documentId, request.VersionId, cancellationToken), StatusCodes.Status202Accepted) : UnauthorizedFailure();

    [HttpPost("{documentId:guid}/jobs/{jobId:guid}/retry")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReindexPolicy)]
    public async Task<IActionResult> RetryJob(Guid documentId, Guid jobId, CancellationToken cancellationToken)
        => Respond(await handlers.RetryJobAsync(documentId, jobId, cancellationToken), StatusCodes.Status202Accepted);

    [HttpPost("{documentId:guid}/archive")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminPublishPolicy)]
    public async Task<IActionResult> Archive(Guid documentId, [FromBody] KnowledgeRevisionRequest request, CancellationToken cancellationToken)
        => Respond(await handlers.ArchiveAsync(documentId, request.Revision, cancellationToken));
}

/// <summary>
/// Admin runtime configuration of the chatbot (generation model and parameters, chunking, retrieval, prompt version).
/// Edit one DRAFT, validate, then activate it atomically; rollback re-activates an older version as a new version.
/// Never returns secrets or service URLs; the embedding model is fixed by the RAG service environment.
/// </summary>
[ApiController]
[Route("api/admin/rag-configuration")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.KnowledgeBase)]
public sealed class AdminRagConfigurationController(RagConfigurationService service) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Respond(Modules.Common.Results.OperationResult<RagConfigurationStateView>.Ok(await service.GetAsync(cancellationToken)));

    [HttpGet("models")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public IActionResult Models() => Respond(Modules.Common.Results.OperationResult<RagModelCatalogView>.Ok(service.Catalog()));

    [HttpGet("versions")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Versions([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Respond(Modules.Common.Results.OperationResult<RagConfigurationHistoryView>.Ok(await service.HistoryAsync(page, pageSize, cancellationToken)));

    [HttpPut("draft")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveRagConfigurationDraftRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } admin) return UnauthorizedFailure();
        var specified = request.Temperature.ValueKind != System.Text.Json.JsonValueKind.Undefined;
        decimal? temperature = null;
        if (request.Temperature.ValueKind == System.Text.Json.JsonValueKind.Number && request.Temperature.TryGetDecimal(out var t)) temperature = t;
        else if (specified && request.Temperature.ValueKind != System.Text.Json.JsonValueKind.Null) return Invalid("temperature", "Must be a number or null.");
        var input = new RagConfigurationInput(request.GenerationModel, specified, temperature, request.MaxOutputTokens, request.ChunkCharacters, request.ChunkOverlap,
            request.TopK, request.MinScore, request.MaxContextTokens, request.MaxHistoryMessages, request.PromptVersion, request.Notes);
        return Respond(await service.SaveDraftAsync(admin, input, request.ExpectedRevision, cancellationToken));
    }

    [HttpPost("draft/validate")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> ValidateDraft(CancellationToken cancellationToken) => Respond(await service.ValidateDraftAsync(cancellationToken));

    [HttpPost("draft/activate")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> Activate([FromBody] ActivateRagConfigurationRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await service.ActivateDraftAsync(admin, request.ExpectedRevision, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("rollback")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> Rollback([FromBody] RollbackRagConfigurationRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await service.RollbackAsync(admin, request.Version, cancellationToken)) : UnauthorizedFailure();

    [HttpDelete("draft")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> DeleteDraft([FromQuery] int? expectedRevision, CancellationToken cancellationToken)
        => Respond(await service.DeleteDraftAsync(expectedRevision, cancellationToken));

    [HttpPost("reindex-stale")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReindexPolicy)]
    public async Task<IActionResult> ReindexStale(CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await service.ReindexStaleAsync(admin, cancellationToken), StatusCodes.Status202Accepted) : UnauthorizedFailure();
}

/// <summary>
/// Grounded chatbot for signed-in users. Answers cite published knowledge only; when the documents
/// do not support an answer the assistant says so instead of guessing. Per-user and global daily
/// quotas and a per-user concurrency cap are enforced atomically in the database.
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
[RequireFeature(FeatureOptions.KnowledgeBase)]
public sealed class ChatController(ChatService chat) : OperationResultControllerBase
{
    [HttpPost("messages")]
    [EnableRateLimiting(RateLimitingExtensions.RagChatPolicy)]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Ask([FromBody] AskChatRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (string.IsNullOrWhiteSpace(request.Message)) return Invalid("message", "Message is required.");
        if (request.ClientMessageId is { Length: > 64 }) return Invalid("clientMessageId", "At most 64 characters.");
        return Respond(await chat.AskAsync(user, request.SessionId, request.Message, request.ClientMessageId, cancellationToken));
    }

    [HttpGet("sessions")]
    [EnableRateLimiting(RateLimitingExtensions.RagHistoryPolicy)]
    public async Task<IActionResult> ListSessions(CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(Modules.Common.Results.OperationResult<IReadOnlyList<ChatSessionView>>.Ok(await chat.ListSessionsAsync(user, cancellationToken)))
            : UnauthorizedFailure();

    [HttpGet("sessions/{sessionId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.RagHistoryPolicy)]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await chat.GetSessionAsync(user, sessionId, cancellationToken)) : UnauthorizedFailure();
}
