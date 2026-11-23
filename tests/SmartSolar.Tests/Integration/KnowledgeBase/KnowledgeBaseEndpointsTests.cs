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
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{Guid.NewGuid()}/publish",
            new { versionId = Guid.NewGuid(), revision = 0, confirmSharedVisibility = true }), HttpStatusCode.NotFound, KnowledgeErrorCodes.DocumentNotFound);
    }

    /// <summary>Registers, ingests and publishes a document for all signed-in roles (explicit confirmation); returns its id and published version.</summary>
    private static async Task<(Guid DocumentId, Guid VersionId)> PublishAsync(AuthApiFactory factory, HttpClient admin, FakeStorageObjectVerifier storage,
        Guid adminId, string title, string[]? tags = null)
    {
        var (documentId, versionId, revision) = await ReadyAsync(factory, admin, storage, adminId, title, tags);
        // Without the explicit confirmation nothing is published.
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/publish", new { versionId, revision }),
            HttpStatusCode.BadRequest, KnowledgeErrorCodes.PublishConfirmationRequired);
        var published = await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/publish",
            new { versionId, revision, confirmSharedVisibility = true, note = "Reviewed for all signed-in roles" }));
        Assert.Equal(SharedVisibilityStatuses.Approved, published.GetProperty("sharedVisibility").GetProperty("status").GetString());
        Assert.Equal(KnowledgeAudiences.Shared, published.GetProperty("sharedVisibility").GetProperty("scope").GetString());
        return (documentId, versionId);
    }

    private static async Task<(Guid DocumentId, Guid VersionId, int Revision)> ReadyAsync(AuthApiFactory factory, HttpClient admin, FakeStorageObjectVerifier storage,
        Guid adminId, string title, string[]? tags = null)
    {
        var file = await UploadAsync(admin, storage, adminId);
        var doc = await DataOf(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title, category = "WARRANTY", tags }),
            HttpStatusCode.Created);
        var documentId = doc.GetProperty("documentId").GetGuid();
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>().RunDueAsync(10, CancellationToken.None));
        var ready = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        Assert.Equal(DocumentStatuses.ReadyForReview, ready.GetProperty("status").GetString());
        Assert.Equal(SharedVisibilityStatuses.NotPublished, ready.GetProperty("sharedVisibility").GetProperty("status").GetString());
        return (documentId, ready.GetProperty("versions")[0].GetProperty("versionId").GetGuid(), ready.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Published_knowledge_answers_customer_questions_with_citations()
    {
        var storage = new FakeStorageObjectVerifier();
        var rag = new FakeRagServiceClient();
        using var factory = Enabled(storage, rag);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var (documentId, versionId) = await PublishAsync(factory, admin, storage, adminId, "Bảo hành");
        // A reviewed-but-unpublished document (READY_FOR_REVIEW): nobody may retrieve it.
        var (draftId, draftVersionId, _) = await ReadyAsync(factory, admin, storage, adminId, "Bản nháp giá nội bộ", [KnowledgeAudiences.LegacyCustomerTag]);
        var draftView = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{draftId}"));
        Assert.True(draftView.GetProperty("legacyAudienceTag").GetBoolean());   // the interim tag grants nothing

        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = " " }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = new string('a', 8001) }), HttpStatusCode.BadRequest,
            KnowledgeErrorCodes.MessageTooLong);

        var answer = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?", clientMessageId = "c-1" }));
        // Retrieval is filtered server-side to the published, approved versions only.
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

        // Staff retrieve exactly the same versions (no role-specific filter): the unpublished draft is excluded for them too.
        await DataOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales).PostAsJsonAsync("/api/chat/messages", new { message = "Giá nội bộ?" }));
        Assert.Equal([versionId], rag.LastAnswerRequest!.AllowedVersions.Select(v => v.VersionId));

        // Even if the RAG service ignored the filter and cited the unpublished draft, nobody gets a citation or a grounded answer from it.
        rag.Answer = request => new RagAnswerResult(true, null, "Giá vốn là ... [1]", true,
            [new RagCitation(draftId, draftVersionId, $"{draftVersionId:N}:0", 1, "Giá vốn", 0.9, "Giá vốn nội bộ")], 10, 10, "fake-llm", 1, 1);
        foreach (var client in new[] { customer, admin })
        {
            var leaked = await DataOf(await client.PostAsJsonAsync("/api/chat/messages", new { message = "Giá vốn tấm pin?" }));
            Assert.False(leaked.GetProperty("grounded").GetBoolean());
            Assert.Equal(0, leaked.GetProperty("answer").GetProperty("citations").GetArrayLength());
            Assert.Equal(ChatService.InsufficientAnswer, leaked.GetProperty("answer").GetProperty("content").GetString());
            Assert.Equal(InsufficientReasons.CitationsRevoked, leaked.GetProperty("insufficientReason").GetString());
        }
        rag.Answer = null;

        // Unpublishing removes the document from answers immediately for everyone; the customer's earlier answer stays readable to its owner
        // but loses its citation (its version is no longer published).
        var current = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/unpublish", new { revision = current.GetProperty("revision").GetInt32() }));
        var history = await DataOf(await customer.GetAsync($"/api/chat/sessions/{sessionId}"));
        var answered = history.GetProperty("messages")[1];
        Assert.False(answered.GetProperty("withheld").GetBoolean());
        Assert.Equal(0, answered.GetProperty("citations").GetArrayLength());
        var after = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?" }));
        Assert.False(after.GetProperty("grounded").GetBoolean());
        Assert.Equal(ChatService.InsufficientAnswer, after.GetProperty("answer").GetProperty("content").GetString());
        Assert.Equal(InsufficientReasons.NoPublishedDocuments, after.GetProperty("insufficientReason").GetString());
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

    [Fact]
    public async Task Every_signed_in_role_retrieves_and_cites_the_same_published_documents_with_its_own_sessions()
    {
        var storage = new FakeStorageObjectVerifier();
        var rag = new FakeRagServiceClient();
        using var factory = Enabled(storage, rag);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var (warrantyId, warrantyVersion) = await PublishAsync(factory, admin, storage, adminId, "Bảo hành");
        var (_, maintenanceVersion) = await PublishAsync(factory, admin, storage, adminId, "Bảo trì");
        var (_, draftVersion, _) = await ReadyAsync(factory, admin, storage, adminId, "Chưa duyệt");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/chat/messages", new { message = "Xin chào" })).StatusCode);
        var invalid = factory.CreateClient();
        invalid.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync("/api/chat/sessions")).StatusCode);

        var expected = new[] { warrantyVersion, maintenanceVersion }.Order().ToList();
        var answers = new List<(string Role, string Content, Guid CitedVersion)>();
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Manager, RoleCodes.Admin })
        {
            var client = role == RoleCodes.Admin ? admin : CatalogEndpointsTests.ClientFor(factory, role);
            var answer = await DataOf(await client.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?" }));
            var sessionId = answer.GetProperty("sessionId").GetGuid();
            // Every role: the same allow-list (both published versions, never the unapproved draft).
            var allowed = rag.LastAnswerRequest!.AllowedVersions.Select(v => v.VersionId).Order().ToList();
            Assert.Equal(expected, allowed);
            Assert.DoesNotContain(draftVersion, allowed);
            Assert.True(answer.GetProperty("grounded").GetBoolean(), role);
            var cited = answer.GetProperty("answer").GetProperty("citations")[0];
            answers.Add((role, answer.GetProperty("answer").GetProperty("content").GetString()!, cited.GetProperty("versionId").GetGuid()));
            // Each user sees only their own session.
            var sessions = await DataOf(await client.GetAsync("/api/chat/sessions"));
            Assert.Equal([sessionId], sessions.EnumerateArray().Select(x => x.GetProperty("sessionId").GetGuid()));
            Assert.Equal(2, (await DataOf(await client.GetAsync($"/api/chat/sessions/{sessionId}"))).GetProperty("messages").GetArrayLength());
        }
        // Identical grounded answer and citation for every role (deterministic fake model).
        Assert.Single(answers.Select(a => (a.Content, a.CitedVersion)).Distinct());
        Assert.Contains(answers[0].CitedVersion, expected);

        // Knowledge and RAG administration stay Admin-only, including the legacy review inventory and the retired audience endpoint.
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Manager })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/rag-configuration")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/knowledge-documents/shared-visibility-review")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/knowledge-documents/{warrantyId}/publish",
                new { versionId = warrantyVersion, revision = 0, confirmSharedVisibility = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/knowledge-documents/{warrantyId}/unpublish", new { revision = 0 })).StatusCode);
        }
        var review = await DataOf(await admin.GetAsync("/api/admin/knowledge-documents/shared-visibility-review"));
        Assert.Equal(0, review.GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task Rag_failures_reach_the_client_as_distinct_codes()
    {
        var storage = new FakeStorageObjectVerifier();
        var rag = new FakeRagServiceClient();
        using var factory = Enabled(storage, rag);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        await PublishAsync(factory, admin, storage, adminId, "Bảo hành");
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        foreach (var (error, status, code) in new[] { ("timeout", HttpStatusCode.GatewayTimeout, KnowledgeErrorCodes.Timeout),
                     ("busy", HttpStatusCode.ServiceUnavailable, KnowledgeErrorCodes.Busy),
                     ("unavailable", HttpStatusCode.ServiceUnavailable, KnowledgeErrorCodes.ServiceUnavailable) })
        {
            rag.Answer = _ => new RagAnswerResult(false, error, null, false, [], 0, 0, null, 0, 0);
            await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?" }), status, code);
        }
        rag.Answer = null;
        Assert.Equal(HttpStatusCode.OK, (await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?" })).StatusCode);
    }
}
