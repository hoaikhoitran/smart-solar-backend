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

    [Fact]
    public async Task Published_knowledge_answers_customer_questions_with_citations()
    {
        var storage = new FakeStorageObjectVerifier();
        var rag = new FakeRagServiceClient();
        using var factory = Enabled(storage, rag);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var file = await UploadAsync(admin, storage, adminId);
        var doc = await DataOf(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title = "Bảo hành", category = "WARRANTY" }),
            HttpStatusCode.Created);
        var documentId = doc.GetProperty("documentId").GetGuid();

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>().RunDueAsync(10, CancellationToken.None));
        var ready = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        Assert.Equal(DocumentStatuses.ReadyForReview, ready.GetProperty("status").GetString());
        var versionId = ready.GetProperty("versions")[0].GetProperty("versionId").GetGuid();
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/publish",
            new { versionId, revision = ready.GetProperty("revision").GetInt32() }));

        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = " " }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await customer.PostAsJsonAsync("/api/chat/messages", new { message = new string('a', 8001) }), HttpStatusCode.BadRequest,
            KnowledgeErrorCodes.MessageTooLong);

        var answer = await DataOf(await customer.PostAsJsonAsync("/api/chat/messages", new { message = "Tấm pin bảo hành bao lâu?", clientMessageId = "c-1" }));
        Assert.True(answer.GetProperty("grounded").GetBoolean());
        var citation = answer.GetProperty("answer").GetProperty("citations")[0];
        Assert.Equal(documentId, citation.GetProperty("documentId").GetGuid());
        Assert.Equal("Bảo hành", citation.GetProperty("documentTitle").GetString());
        var sessionId = answer.GetProperty("sessionId").GetGuid();

        Assert.Equal(1, (await DataOf(await customer.GetAsync("/api/chat/sessions"))).GetArrayLength());
        Assert.Equal(2, (await DataOf(await customer.GetAsync($"/api/chat/sessions/{sessionId}"))).GetProperty("messages").GetArrayLength());
        var stranger = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        await AssertFailureAsync(await stranger.GetAsync($"/api/chat/sessions/{sessionId}"), HttpStatusCode.NotFound, KnowledgeErrorCodes.SessionNotFound);

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
