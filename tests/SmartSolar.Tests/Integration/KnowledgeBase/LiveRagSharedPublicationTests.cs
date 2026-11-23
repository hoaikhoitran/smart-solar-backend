using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// Runs only when SMARTSOLAR_TEST_RAG = "baseUrl|sharedSecret|localStorageDir" points at a LOCAL development RAG service
/// (RAG_ENV=development, RAG_STORAGE=local, hashing embeddings, extractive model, in-memory or pgvector store).
/// </summary>
public sealed class LiveRagFactAttribute : FactAttribute
{
    public const string Variable = "SMARTSOLAR_TEST_RAG";

    public LiveRagFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable}=baseUrl|sharedSecret|localStorageDir to a local development RAG service to run.";
    }
}

/// <summary>
/// Shared published knowledge END TO END with the REAL RAG service (no fake client): the API's HMAC-signed client, real ingestion of the
/// stored file, real vector retrieval filtered by the API's allow-list, real (extractive, development) generation and citation validation.
/// Five roles ask the same question; unpublished, unapproved legacy and archived content never leaks; reason codes come from real conditions.
/// </summary>
public sealed class LiveRagSharedPublicationTests
{
    private const string Published = "Tấm pin mặt trời SmartSolar được bảo hành sản phẩm 12 năm và bảo hành hiệu suất 25 năm kể từ ngày nghiệm thu.";
    private const string Draft = "Bảng giá vốn nội bộ: chiết khấu đại lý tấm pin là 18 phần trăm, không công bố cho khách hàng.";
    private const string Legacy = "Quy trình nội bộ cũ: mật mã tủ điện kho Thủ Đức là 4711, chỉ dành cho nhân viên kho.";

    private static (string BaseUrl, string Secret, string StorageDir) Live()
    {
        var parts = Environment.GetEnvironmentVariable(LiveRagFactAttribute.Variable)!.Split('|');
        return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
    }

    private static AuthApiFactory Factory(FakeStorageObjectVerifier storage)
    {
        var (baseUrl, secret, _) = Live();
        var settings = new Dictionary<string, string?>
        {
            ["Features:InstallationWorkflowEnabled"] = "true",
            ["Features:KnowledgeBaseEnabled"] = "true",
            ["Storage:DefaultBucket"] = Bucket,
            ["Rag:Enabled"] = "true",
            ["Rag:ServiceBaseUrl"] = baseUrl,
            ["Rag:ServiceSharedSecret"] = secret,
            ["Rag:EmbeddingModel"] = "hashing-dev",
            ["Rag:IngestionWorkerEnabled"] = "false",
            ["RateLimiting:RagChat:PermitLimit"] = "100",
        };
        // The real IRagServiceClient registered by the API is kept: only storage verification is faked.
        return new AuthApiFactory(settings)
        {
            ConfigureTestServices = collection =>
            {
                collection.RemoveAll<IStorageObjectVerifier>();
                collection.AddSingleton<IStorageObjectVerifier>(storage);
            },
        };
    }

    /// <summary>Uploads a text document both to the fake verifier and to the RAG service's local object store, then registers and ingests it.</summary>
    private static async Task<(Guid DocumentId, Guid VersionId, int Revision)> ReadyAsync(AuthApiFactory factory, HttpClient admin, FakeStorageObjectVerifier storage,
        Guid adminId, string title, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var key = $"{FilePolicies.ByPurpose[FilePurposes.KnowledgeDocument].KeyPrefix}{adminId}/{Guid.NewGuid():N}.txt";
        storage.Put(Bucket, key, bytes);
        var path = Path.Combine(Live().StorageDir, Bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var file = (await DataOf(await admin.PostAsJsonAsync("/api/files", new
        {
            purpose = FilePurposes.KnowledgeDocument, objectKey = key, fileName = "doc.txt", contentType = FilePolicies.PlainText, sizeBytes = bytes.Length,
        }), HttpStatusCode.Created)).GetProperty("fileId").GetGuid();
        var doc = await DataOf(await admin.PostAsJsonAsync("/api/admin/knowledge-documents", new { fileId = file, title, category = "WARRANTY" }), HttpStatusCode.Created);
        var documentId = doc.GetProperty("documentId").GetGuid();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>().RunDueAsync(10, CancellationToken.None);
        var ready = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{documentId}"));
        Assert.True(ready.GetProperty("status").GetString() == DocumentStatuses.ReadyForReview, ready.ToString());
        return (documentId, ready.GetProperty("versions")[0].GetProperty("versionId").GetGuid(), ready.GetProperty("revision").GetInt32());
    }

    private static async Task<System.Text.Json.JsonElement> AskAsync(HttpClient client, string question)
        => await DataOf(await client.PostAsJsonAsync("/api/chat/messages", new { message = question }));

    private static string Text(System.Text.Json.JsonElement answer) => answer.GetProperty("answer").GetProperty("content").GetString()!;

    [LiveRagFact]
    public async Task Five_roles_get_the_same_grounded_answer_and_nothing_unapproved_ever_leaks()
    {
        var storage = new FakeStorageObjectVerifier();
        using var factory = Factory(storage);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        // Nothing published yet: honest insufficient answer for everyone, no model call needed.
        var empty = await AskAsync(customer, "Tấm pin được bảo hành bao nhiêu năm?");
        Assert.False(empty.GetProperty("grounded").GetBoolean());
        Assert.Equal(InsufficientReasons.NoPublishedDocuments, empty.GetProperty("insufficientReason").GetString());

        var (warrantyId, warrantyVersion, revision) = await ReadyAsync(factory, admin, storage, adminId, "Bảo hành", Published);
        // Publishing without the explicit shared confirmation is refused; with it, allowed and audited.
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{warrantyId}/publish", new { versionId = warrantyVersion, revision }),
            HttpStatusCode.BadRequest, KnowledgeErrorCodes.PublishConfirmationRequired);
        var published = await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{warrantyId}/publish",
            new { versionId = warrantyVersion, revision, confirmSharedVisibility = true, note = "Live E2E review" }));
        Assert.Equal(KnowledgePublicationActions.ApprovedAllSignedInRoles, published.GetProperty("publicationApprovals")[0].GetProperty("action").GetString());

        // A second document is ingested (chunks exist in the vector store) but NOT published.
        await ReadyAsync(factory, admin, storage, adminId, "Giá vốn", Draft);
        // A third one is published, then turned into an unreviewed legacy document (as after the cutover migration).
        var (legacyId, legacyVersion, legacyRevision) = await ReadyAsync(factory, admin, storage, adminId, "Quy trình kho", Legacy);
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{legacyId}/publish",
            new { versionId = legacyVersion, revision = legacyRevision, confirmSharedVisibility = true }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.KnowledgeDocumentVersions.Where(v => v.Id == legacyVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.SharedScopeApprovedAt, (DateTimeOffset?)null).SetProperty(v => v.SharedScopeApprovedByUserId, (Guid?)null));
            await db.KnowledgeDocuments.Where(d => d.Id == legacyId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Audience, KnowledgeAudiences.Internal));
        }
        Assert.Equal(1, (await DataOf(await admin.GetAsync("/api/admin/knowledge-documents/shared-visibility-review"))).GetProperty("pendingCount").GetInt32());

        // Five roles, same question, same grounded answer and citation from the real service.
        var answers = new List<(string Content, Guid Version)>();
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
        {
            var client = role == RoleCodes.Admin ? admin : CatalogEndpointsTests.ClientFor(factory, role);
            var answer = await AskAsync(client, "Tấm pin được bảo hành sản phẩm bao nhiêu năm?");
            Assert.True(answer.GetProperty("grounded").GetBoolean(), $"{role}: {answer}");
            var citation = answer.GetProperty("answer").GetProperty("citations")[0];
            Assert.Equal(warrantyVersion, citation.GetProperty("versionId").GetGuid());
            Assert.Contains("12", Text(answer));
            answers.Add((Text(answer), citation.GetProperty("versionId").GetGuid()));
        }
        Assert.Single(answers.Distinct());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/chat/messages", new { message = "Bảo hành?" })).StatusCode);

        // Unpublished and unreviewed legacy content: no answer text, snippet or citation from them, for a customer or for staff.
        foreach (var client in new[] { customer, admin })
        {
            foreach (var question in new[] { "Chiết khấu đại lý tấm pin là bao nhiêu phần trăm?", "Mật mã tủ điện kho Thủ Đức là gì?" })
            {
                var leaked = await AskAsync(client, question);
                var body = leaked.ToString();
                Assert.DoesNotContain("18 phần trăm", body);
                Assert.DoesNotContain("4711", body);
                Assert.All(leaked.GetProperty("answer").GetProperty("citations").EnumerateArray(),
                    c => Assert.Equal(warrantyVersion, c.GetProperty("versionId").GetGuid()));
            }
        }

        // An unrelated question: chunks exist but none is relevant enough (real retrieval score).
        var unrelated = await AskAsync(customer, "Thời tiết Hà Nội ngày mai thế nào?");
        Assert.False(unrelated.GetProperty("grounded").GetBoolean());
        Assert.Contains(unrelated.GetProperty("insufficientReason").GetString(),
            new[] { InsufficientReasons.BelowMinScore, InsufficientReasons.ModelInsufficient });

        // Admin approves the legacy document after review: shared access activated for every role.
        var legacyView = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{legacyId}"));
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{legacyId}/publish",
            new { versionId = legacyVersion, revision = legacyView.GetProperty("revision").GetInt32(), confirmSharedVisibility = true, note = "Reviewed" }));
        var approved = await AskAsync(customer, "Mật mã tủ điện kho Thủ Đức là gì?");
        Assert.True(approved.GetProperty("grounded").GetBoolean(), approved.ToString());
        Assert.Equal(legacyVersion, approved.GetProperty("answer").GetProperty("citations")[0].GetProperty("versionId").GetGuid());

        // Archive: no subsequent retrieval for anyone.
        var current = await DataOf(await admin.GetAsync($"/api/admin/knowledge-documents/{warrantyId}"));
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{warrantyId}/archive", new { revision = current.GetProperty("revision").GetInt32() }));
        var afterArchive = await AskAsync(CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician), "Tấm pin được bảo hành sản phẩm bao nhiêu năm?");
        Assert.All(afterArchive.GetProperty("answer").GetProperty("citations").EnumerateArray(),
            c => Assert.NotEqual(warrantyVersion, c.GetProperty("versionId").GetGuid()));
        Assert.DoesNotContain("25 năm", Text(afterArchive));
    }

    [LiveRagFact]
    public async Task A_reindex_required_document_reports_no_indexed_chunks_from_the_real_store()
    {
        var storage = new FakeStorageObjectVerifier();
        using var factory = Factory(storage);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var (documentId, versionId, revision) = await ReadyAsync(factory, admin, storage, adminId, "Bảo hành", Published);
        await DataOf(await admin.PostAsJsonAsync($"/api/admin/knowledge-documents/{documentId}/publish",
            new { versionId, revision, confirmSharedVisibility = true }));
        // The vectors of the published version disappear from the store (e.g. a model change awaiting reindex).
        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IRagServiceClient>().DeleteVectorsAsync(versionId, CancellationToken.None));
        var answer = await AskAsync(CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer), "Tấm pin được bảo hành sản phẩm bao nhiêu năm?");
        Assert.False(answer.GetProperty("grounded").GetBoolean());
        Assert.Equal(InsufficientReasons.NoIndexedChunks, answer.GetProperty("insufficientReason").GetString());
    }
}
