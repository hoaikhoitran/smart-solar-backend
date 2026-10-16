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
