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

    /// <summary>Reclassifies who may retrieve the document (INTERNAL or CUSTOMER) with a reason; audited, effective immediately.</summary>
    [HttpPut("{documentId:guid}/audience")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminPublishPolicy)]
    public async Task<IActionResult> SetAudience(Guid documentId, [FromBody] SetKnowledgeAudienceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin
            ? Respond(await handlers.SetAudienceAsync(admin, documentId, request.Revision, request.Audience, request.Reason, cancellationToken))
            : UnauthorizedFailure();

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
        var effortSpecified = request.ReasoningEffort.ValueKind != System.Text.Json.JsonValueKind.Undefined;
        if (effortSpecified && request.ReasoningEffort.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null))
            return Invalid("reasoningEffort", "Must be a string or null.");
        var input = new RagConfigurationInput(request.GenerationModel, specified, temperature, request.MaxOutputTokens, request.ChunkCharacters, request.ChunkOverlap,
            request.TopK, request.MinScore, request.MaxContextTokens, request.MaxHistoryMessages, request.PromptVersion, request.Notes, effortSpecified,
            request.ReasoningEffort.ValueKind == System.Text.Json.JsonValueKind.String ? request.ReasoningEffort.GetString() : null, request.GenerationEnabled,
            request.UserRequestsPerDay, request.UserConcurrency, request.GlobalConcurrency, request.MaxQuestionCharacters, request.GenerationTimeoutSeconds,
            request.DailyBudgetUsd, request.MonthlyBudgetUsd, request.MonthlyIngestionBudgetUsd);
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
        if (request.Advisor?.Problem() is { } problem) return Invalid(problem.Field, problem.Message);
        return Respond(await chat.AskAsync(user, request.SessionId, request.Message, request.ClientMessageId, cancellationToken, Audience(),
            request.Advisor?.ToInput(), HighestRole()));
    }

    /// <summary>
    /// Structured configuration advice without a chat message (same deterministic advisor, nothing stored). Every signed-in role may use it;
    /// it reads only ACTIVE catalog data, the caller's own site and PVGIS, and never writes.
    /// </summary>
    [HttpPost("advisor")]
    [EnableRateLimiting(RateLimitingExtensions.RagChatPolicy)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Advise([FromBody] SolarAdvisorRequest request, [FromServices] SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorService advisor,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Problem() is { } problem) return Invalid(problem.Field.Replace("advisor.", ""), problem.Message);
        return Respond(Modules.Common.Results.OperationResult<SmartSolar.Modules.KnowledgeBase.Advisor.SolarAdvisorView>.Ok(
            await advisor.AdviseAsync(user, request.ToInput(), cancellationToken)));
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
        => CurrentUserId() is { } user ? Respond(await chat.GetSessionAsync(user, sessionId, cancellationToken, Audience())) : UnauthorizedFailure();

    /// <summary>The caller's highest role, recorded on the cost ledger (role breakdown of the Admin dashboard).</summary>
    private string? HighestRole()
        => new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer }.FirstOrDefault(IsInRole);

    /// <summary>Staff roles read every published document; anyone else (customers, or a token without a staff role) only customer-visible ones.</summary>
    private KnowledgeAudience Audience()
        => IsInRole(RoleCodes.Admin) || IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Sales) || IsInRole(RoleCodes.Technician)
            ? KnowledgeAudience.Staff
            : KnowledgeAudience.Customer;
}

/// <summary>
/// Admin AI cost dashboard over the cost ledger (server-side aggregation, UTC date ranges of at most 92 days). No prompt, answer or provider
/// secret is ever returned. Admin only (Customer, Technician, Sales and Manager get 403).
/// </summary>
[ApiController]
[Route("api/admin/rag-usage")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.KnowledgeBase)]
public sealed class AdminRagUsageController(AiUsageDashboardService dashboard) : OperationResultControllerBase
{
    [HttpGet("summary")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await dashboard.SummaryAsync(from, to, cancellationToken));

    [HttpGet("timeseries")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Timeseries([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await dashboard.TimeseriesAsync(from, to, cancellationToken));

    [HttpGet("by-model")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> ByModel([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await dashboard.BreakdownAsync("model", from, to, cancellationToken));

    [HttpGet("by-operation")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> ByOperation([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await dashboard.BreakdownAsync("operation", from, to, cancellationToken));

    [HttpGet("by-role")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> ByRole([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await dashboard.BreakdownAsync("role", from, to, cancellationToken));

    [HttpGet("top-users")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> TopUsers([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int limit = 10, CancellationToken cancellationToken = default)
        => Respond(await dashboard.TopUsersAsync(from, to, limit, cancellationToken));

    [HttpGet("requests")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Requests([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? operation, [FromQuery] string? status,
        [FromQuery] string? outcome, [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Respond(await dashboard.RequestsAsync(from, to, operation?.Trim().ToUpperInvariant(), status?.Trim().ToUpperInvariant(), outcome?.Trim().ToUpperInvariant(),
            userId, page, pageSize, cancellationToken));

    /// <summary>Settles an UNCERTAIN charge at the cost confirmed by the provider's billing (audited: who, when, note).</summary>
    [HttpPost("{ledgerId:guid}/review")]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> Review(Guid ledgerId, [FromBody] ReviewAiUsageRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await dashboard.ReviewAsync(admin, ledgerId, request.CostUsd, request.Note, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>
/// AI budget administration: GET shows the active cost controls, the hard ceilings, current spending and alerts; PUT changes the cost
/// controls as a new, audited, rollback-able configuration version (values above the ceilings are refused). Admin only.
/// </summary>
[ApiController]
[Route("api/admin/rag-budget")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.KnowledgeBase)]
public sealed class AdminRagBudgetController(AiUsageDashboardService dashboard, RagConfigurationService configuration) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminReadPolicy)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Respond(Modules.Common.Results.OperationResult<AiBudgetView>.Ok(await dashboard.BudgetAsync(cancellationToken)));

    [HttpPut]
    [EnableRateLimiting(RateLimitingExtensions.RagAdminConfigPolicy)]
    public async Task<IActionResult> Update([FromBody] UpdateRagBudgetRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } admin) return UnauthorizedFailure();
        if (request.ExpectedActiveVersion is not { } expected || expected < 0) return Invalid("expectedActiveVersion", "Required: the activeVersion read from GET /api/admin/rag-budget.");
        var input = new RagConfigurationInput(null, false, null, null, null, null, null, null, null, null, null, null, GenerationEnabled: request.GenerationEnabled,
            UserRequestsPerDay: request.UserRequestsPerDay, UserConcurrency: request.UserConcurrency, GlobalConcurrency: request.GlobalConcurrency,
            MaxQuestionCharacters: request.MaxQuestionCharacters, GenerationTimeoutSeconds: request.GenerationTimeoutSeconds, DailyBudgetUsd: request.DailyBudgetUsd,
            MonthlyBudgetUsd: request.MonthlyBudgetUsd, MonthlyIngestionBudgetUsd: request.MonthlyIngestionBudgetUsd);
        return Respond(await configuration.SetCostControlsAsync(admin, input, expected, request.Notes, cancellationToken));
    }
}
