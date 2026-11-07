using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Files;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>Knowledge base through the real pipeline: feature gate, admin RBAC, file ownership, grounded chat, rate limits. Storage and RAG are synthetic.</summary>
public sealed class KnowledgeBaseEndpointsTests
{
    [Fact]
    public async Task Knowledge_base_endpoints_answer_503_until_the_migration_is_applied()
    {
        using var factory = new AuthApiFactory();
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync("/api/chat/sessions"), HttpStatusCode.ServiceUnavailable,
            "FEATURE_PENDING_MIGRATION");
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin).GetAsync("/api/admin/knowledge-documents"), HttpStatusCode.ServiceUnavailable,
            "FEATURE_PENDING_MIGRATION");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/chat/sessions")).StatusCode);
    }

    [Fact]
    public async Task Knowledge_administration_is_admin_only_and_files_must_belong_to_the_caller()
    {
        var storage = new FakeStorageObjectVerifier();
        using var factory = Enabled(storage, new FakeRagServiceClient());
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Technician })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).GetAsync("/api/admin/knowledge-documents")).StatusCode);

        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        Assert.Equal(0, (await DataOf(await admin.GetAsync("/api/admin/knowledge-documents"))).GetArrayLength());

        var otherKey = $"knowledge/{Guid.NewGuid()}/x.pdf";
        storage.Put(Bucket, otherKey, FakeStorageObjectVerifier.PdfBytes);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/files", new
        {
            purpose = FilePurposes.KnowledgeDocument, objectKey = otherKey, fileName = "x.pdf", contentType = FilePolicies.Pdf, sizeBytes = 10,
        }), HttpStatusCode.BadRequest, "FILE_INVALID_OBJECT_KEY");

        // Another admin's verified file cannot be used either.
        var otherAdminId = Guid.NewGuid();
        var foreignFile = await UploadAsync(CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, otherAdminId), storage, otherAdminId);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = foreignFile, title = "T", category = "FAQ" }),
            HttpStatusCode.Forbidden, "FILE_NOT_OWNED");

        var file = await UploadAsync(admin, storage, adminId);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title = "T", category = "NOPE" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var doc = await DataOf(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title = "Bảo hành", category = "WARRANTY" }),
            HttpStatusCode.Created);
        Assert.Equal(DocumentStatuses.Registered, doc.GetProperty("status").GetString());
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{Guid.NewGuid()}/publish", new { versionId = Guid.NewGuid(), revision = 0 }),
            HttpStatusCode.NotFound, KnowledgeErrorCodes.DocumentNotFound);
    }

    /// <summary>Registers, ingests and publishes a document; returns its id and published version.</summary>
    private static async Task<(Guid DocumentId, Guid VersionId)> PublishAsync(AuthApiFactory factory, HttpClient admin, FakeStorageObjectVerifier storage,
        Guid adminId, string title, string audience, string[]? tags = null)
    {
        var file = await UploadAsync(admin, storage, adminId);
        var doc = await DataOf(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title, category = "WARRANTY", tags }),
            HttpStatusCode.Created);
        var documentId = doc.GetProperty("documentId").GetGuid();
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>().RunDueAsync(10, CancellationToken.None));
        var ready = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        Assert.Equal(DocumentStatuses.ReadyForReview, ready.GetProperty("status").GetString());
        var versionId = ready.GetProperty("versions")[0].GetProperty("versionId").GetGuid();
        var published = await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/publish",
            new { versionId, revision = ready.GetProperty("revision").GetInt32() }));
        Assert.Equal(KnowledgeAudiences.Internal, published.GetProperty("audience").GetString());   // safe default
        if (audience != KnowledgeAudiences.Internal)
            await SetAudienceAsync(admin, documentId, audience, "Reviewed: customer-safe content");
        return (documentId, versionId);
    }

    private static async Task<System.Text.Json.JsonElement> SetAudienceAsync(HttpClient admin, Guid documentId, string audience, string reason)
    {
        var current = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        return await DataOf(await admin.PutAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/audience",
            new { audience, reason, revision = current.GetProperty("revision").GetInt32() }));
    }

    [Fact]
    public async Task Published_knowledge_answers_customer_questions_with_citations()
    {
        var storage = new FakeStorageObjectVerifier();
        var rag = new FakeRagServiceClient();
        using var factory = Enabled(storage, rag);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        // D-07 / M-2: the Admin classifies the warranty document CUSTOMER; the pricing guide stays INTERNAL even though it carries the
        // interim audience tag, which grants nothing any more (it is only flagged for review).
        var (documentId, versionId) = await PublishAsync(factory, admin, storage, adminId, "Bảo hành", KnowledgeAudiences.Customer);
        var (internalId, internalVersionId) = await PublishAsync(factory, admin, storage, adminId, "Internal pricing guide", KnowledgeAudiences.Internal,
            [KnowledgeAudiences.LegacyCustomerTag]);
        var internalView = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{internalId}"));
        Assert.Equal((KnowledgeAudiences.Internal, true), (internalView.GetProperty("audience").GetString(), internalView.GetProperty("legacyAudienceTag").GetBoolean()));
        var classified = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        var change = System.Linq.Enumerable.Single(classified.GetProperty("audienceChanges").EnumerateArray());
        Assert.Equal((KnowledgeAudiences.Internal, KnowledgeAudiences.Customer, adminId, "Reviewed: customer-safe content"),
            (change.GetProperty("fromAudience").GetString(), change.GetProperty("toAudience").GetString(), change.GetProperty("actorUserId").GetGuid(),
             change.GetProperty("reason").GetString()));

        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = " " }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = new string('a', 8001) }), HttpStatusCode.BadRequest,
            KnowledgeErrorCodes.MessageTooLong);

        var answer = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?", clientMessageId = "c-1" }));
        // Retrieval for a customer is filtered server-side to customer-visible documents only.
        Assert.Equal([versionId], rag.LastAnswerRequest!.AllowedVersions.Select(v => v.VersionId));
        Assert.True(answer.GetProperty("grounded").GetBoolean());
        var citation = answer.GetProperty("answer").GetProperty("citations")[0];
        Assert.Equal(documentId, citation.GetProperty("documentId").GetGuid());
        Assert.Equal("Bảo hành", citation.GetProperty("documentTitle").GetString());
        var sessionId = answer.GetProperty("sessionId").GetGuid();

        Assert.Equal(1, (await DataOf(await customer.GetAsync("/api/chat/sessions"))).GetArrayLength());
        Assert.Equal(2, (await DataOf(await customer.GetAsync($"/api/chat/sessions/{sessionId}"))).GetProperty("messages").GetArrayLength());
        var stranger = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        await AssertFailureAsync(await stranger.GetAsync($"/api/chat/sessions/{sessionId}"), HttpStatusCode.NotFound, KnowledgeErrorCodes.SessionNotFound);

        // Staff retrieve every published document, internal ones included.
        await DataOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales).PostAsJsonAsync("/api/chat/messages", new { message = "Giá nội bộ?" }));
        Assert.Equal(new[] { documentId, internalId }.Order(), rag.LastAnswerRequest!.AllowedVersions.Select(v => v.DocumentId).Order());

        // Even if the RAG service ignored the filter and cited the internal document, the customer gets no citation and no grounded answer.
        rag.Answer = request => new RagAnswerResult(true, null, "Giá vốn là ... [1]", true,
            [new RagCitation(internalId, internalVersionId, $"{internalVersionId:N}:0", 1, "Giá vốn", 0.9, "Giá vốn nội bộ")], 10, 10, "fake-llm", 1, 1);
        var leaked = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Giá vốn tấm pin?" }));
        Assert.False(leaked.GetProperty("grounded").GetBoolean());
        Assert.Equal(0, leaked.GetProperty("answer").GetProperty("citations").GetArrayLength());
        Assert.Equal(ChatService.InsufficientAnswer, leaked.GetProperty("answer").GetProperty("content").GetString());
        rag.Answer = null;

        // Reclassifying the warranty document INTERNAL revokes it at once: the customer's earlier answer built from it is withheld (text and
        // citations), while the stored row is kept for review; the staff still see the document.
        await SetAudienceAsync(admin, documentId, KnowledgeAudiences.Internal, "Contains supplier terms");
        var history = await DataOf(await customer.GetAsync($"/api/chat/sessions/{sessionId}"));
        var answered = history.GetProperty("messages")[1];
        Assert.True(answered.GetProperty("withheld").GetBoolean());
        Assert.Equal(ChatService.WithheldAnswer, answered.GetProperty("content").GetString());
        Assert.Equal(0, answered.GetProperty("citations").GetArrayLength());
        var retried = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?", clientMessageId = "c-1", sessionId }));
        Assert.Equal(ChatService.WithheldAnswer, retried.GetProperty("answer").GetProperty("content").GetString());   // the idempotent replay too
        await SetAudienceAsync(admin, documentId, KnowledgeAudiences.Customer, "Supplier terms removed in v1");

        // Unpublishing removes the document from answers immediately.
        var current = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/unpublish", new { revision = current.GetProperty("revision").GetInt32() }));
        var after = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?" }));
        Assert.False(after.GetProperty("grounded").GetBoolean());
        Assert.Equal(ChatService.InsufficientAnswer, after.GetProperty("answer").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Chat_is_rate_limited_per_user()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new Dictionary<string, string?>
        {
            ["RateLimiting:RagChat:PermitLimit"] = "2",
        });
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        Assert.Equal(HttpStatusCode.OK, (await customer.PostAsJsonAsync("/api/chat/messages", new { message = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.PostAsJsonAsync("/api/chat/messages", new { message = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await customer.PostAsJsonAsync("/api/chat/messages", new { message = "3" })).StatusCode);
        // Limits are per user, not global.
        Assert.Equal(HttpStatusCode.OK, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).PostAsJsonAsync("/api/chat/messages", new { message = "4" })).StatusCode);
    }
}
