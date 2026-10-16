using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

public sealed class KnowledgeBaseTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig(InstallationWorkflowTestContext ctx)
    {
        public InstallationWorkflowTestContext Ctx { get; } = ctx;
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32) };

        public KnowledgeDocumentHandlers Documents() => new(new KnowledgeUnitOfWork(Ctx.Db), Ctx.Files(), Options, Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        public KnowledgeIngestionService Ingestion() => new(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance);
        public ChatService Chat() => new(new KnowledgeUnitOfWork(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<ChatService>.Instance);

        public async Task<Guid> AdminAsync() => (await Ctx.Scheduling.SeedTechnicianAsync($"Admin {Guid.NewGuid():N}", null, withRole: false)).Id;

        /// <summary>Registered, ingested (READY_FOR_REVIEW) and published.</summary>
        public async Task<KnowledgeDocumentView> PublishedAsync(Guid admin, string title = "Chính sách bảo hành")
        {
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = await Documents().RegisterAsync(admin, file, title, "warranty", ["pin"], None);
            Assert.True(registered.Succeeded, registered.Message);
            Assert.Equal(1, await Ingestion().RunDueAsync(10, None));
            var ready = (await Documents().GetAsync(registered.Value!.DocumentId, None)).Value!;
            Assert.Equal(DocumentStatuses.ReadyForReview, ready.Status);
            var published = await Documents().PublishAsync(ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, None);
            Assert.True(published.Succeeded, published.Message);
            return published.Value!;
        }
    }

    [Fact]
    public async Task Registration_ingestion_and_publication_make_a_version_retrievable()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);

        Assert.Equal(DocumentStatuses.Published, doc.Status);
        Assert.Equal("WARRANTY", doc.Category);
        var version = Assert.Single(doc.Versions!);
        Assert.Equal(VersionStatuses.Published, version.Status);
        Assert.Equal(12, version.ChunkCount);
        var allowed = await new KnowledgeUnitOfWork(ctx.Db).GetRetrievableVersionsAsync(None);
        Assert.Equal(version.VersionId, Assert.Single(allowed).VersionId);
    }

    [Fact]
    public async Task Registration_rejects_unverified_foreign_and_wrong_purpose_files_and_bad_metadata()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var other = await rig.AdminAsync();
        var foreign = await ctx.VerifiedFileAsync(other, FilePurposes.KnowledgeDocument, "pdf");
        var wrongPurpose = await ctx.VerifiedFileAsync(admin, FilePurposes.ContractDocument, "pdf");
        var good = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");

        Assert.False((await rig.Documents().RegisterAsync(admin, foreign, "T", "FAQ", null, None)).Succeeded);
        Assert.False((await rig.Documents().RegisterAsync(admin, wrongPurpose, "T", "FAQ", null, None)).Succeeded);
        Assert.Equal("VALIDATION_FAILED", (await rig.Documents().RegisterAsync(admin, good, "T", "NOT_A_CATEGORY", null, None)).ErrorCode);
        Assert.Equal("VALIDATION_FAILED", (await rig.Documents().RegisterAsync(admin, good, "  ", "FAQ", null, None)).ErrorCode);
        Assert.True((await rig.Documents().RegisterAsync(admin, good, "T", "FAQ", null, None)).Succeeded);
        // The same file cannot back two versions.
        Assert.False((await rig.Documents().RegisterAsync(admin, good, "T2", "FAQ", null, None)).Succeeded);
    }

    [Fact]
    public async Task Only_ready_versions_can_be_published_and_stale_revisions_are_rejected()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var doc = (await rig.Documents().RegisterAsync(admin, file, "FAQ", "FAQ", null, None)).Value!;

        Assert.Equal(KnowledgeErrorCodes.InvalidState, (await rig.Documents().PublishAsync(doc.DocumentId, doc.Versions![0].VersionId, doc.Revision, None)).ErrorCode);
        await rig.Ingestion().RunDueAsync(10, None);
        var ready = (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!;
        Assert.Equal(KnowledgeErrorCodes.StaleRevision, (await rig.Documents().PublishAsync(doc.DocumentId, doc.Versions![0].VersionId, doc.Revision, None)).ErrorCode);
        Assert.True((await rig.Documents().PublishAsync(doc.DocumentId, doc.Versions![0].VersionId, ready.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task Scanned_documents_are_marked_ocr_required_and_cannot_be_published()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Rag.IngestStatus = "OCR_REQUIRED";
        var admin = await rig.AdminAsync();
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var doc = (await rig.Documents().RegisterAsync(admin, file, "Scan", "OTHER", null, None)).Value!;
        await rig.Ingestion().RunDueAsync(10, None);

        var after = (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!;
        Assert.Equal(VersionStatuses.OcrRequired, after.Versions![0].Status);
        Assert.Equal(DocumentStatuses.Failed, after.Status);
        Assert.False((await rig.Documents().PublishAsync(doc.DocumentId, after.Versions![0].VersionId, after.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task Ingestion_failures_back_off_then_fail_and_can_be_retried_manually()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Rag.Unavailable = true;
        var admin = await rig.AdminAsync();
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var doc = (await rig.Documents().RegisterAsync(admin, file, "Guide", "INSTALLATION_GUIDE", null, None)).Value!;

        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        var job = await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.RetryWaiting, job.Status);
        Assert.Equal(ctx.Clock.GetUtcNow().AddSeconds(30), job.NextAttemptAt);
        // Not due yet: nothing runs.
        Assert.Equal(0, await rig.Ingestion().RunDueAsync(10, None));

        for (var i = 1; i < job.MaxAttempts; i++)
        {
            ctx.Clock.Now += TimeSpan.FromHours(2);
            Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        }
        job = await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Failed, job.Status);
        Assert.Equal(DocumentStatuses.Failed, (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!.Status);

        rig.Rag.Unavailable = false;
        Assert.True((await rig.Documents().RetryJobAsync(doc.DocumentId, job.Id, None)).Succeeded);
        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        Assert.Equal(DocumentStatuses.ReadyForReview, (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!.Status);
    }

    [Fact]
    public async Task A_leased_job_is_not_taken_by_a_second_worker_until_the_lease_expires()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        await rig.Documents().RegisterAsync(admin, file, "Guide", "FAQ", null, None);
        var job = await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync();
        var uow = new KnowledgeUnitOfWork(ctx.Db);
        var now = ctx.Clock.GetUtcNow();

        Assert.True(await uow.TryLeaseJobAsync(job.Id, now, now.AddMinutes(10), None));
        Assert.False(await uow.TryLeaseJobAsync(job.Id, now, now.AddMinutes(10), None));
        Assert.False(await rig.Ingestion().RunAsync(job.Id, None));
        Assert.Equal(0, rig.Rag.IngestCalls);
        // A crashed worker's lease expires and the job is picked up again.
        ctx.Clock.Now += TimeSpan.FromMinutes(11);
        Assert.True(await rig.Ingestion().RunAsync(job.Id, None));
        Assert.Equal(1, rig.Rag.IngestCalls);
    }

    [Fact]
    public async Task Unpublish_and_archive_remove_documents_from_retrieval_and_queue_vector_deletion()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        var uow = new KnowledgeUnitOfWork(ctx.Db);

        var unpublished = await rig.Documents().UnpublishAsync(doc.DocumentId, doc.Revision, None);
        Assert.True(unpublished.Succeeded, unpublished.Message);
        Assert.Empty(await uow.GetRetrievableVersionsAsync(None));
        // Re-publishing a withdrawn version is allowed without re-ingestion.
        var republished = await rig.Documents().PublishAsync(doc.DocumentId, doc.Versions![0].VersionId, unpublished.Value!.Revision, None);
        Assert.True(republished.Succeeded, republished.Message);
        Assert.Single(await uow.GetRetrievableVersionsAsync(None));

        var archived = await rig.Documents().ArchiveAsync(doc.DocumentId, republished.Value!.Revision, None);
        Assert.True(archived.Succeeded, archived.Message);
        Assert.Empty(await uow.GetRetrievableVersionsAsync(None));
        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        Assert.Equal(1, rig.Rag.DeleteCalls);
        Assert.False((await rig.Documents().PublishAsync(doc.DocumentId, doc.Versions![0].VersionId, archived.Value!.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task Publishing_a_new_version_supersedes_the_old_one()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        var file2 = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        Assert.True((await rig.Documents().AddVersionAsync(admin, doc.DocumentId, file2, None)).Succeeded);
        // The old version stays live while the new one is processed.
        Assert.Equal(doc.Versions![0].VersionId, Assert.Single(await new KnowledgeUnitOfWork(ctx.Db).GetRetrievableVersionsAsync(None)).VersionId);
        await rig.Ingestion().RunDueAsync(10, None);
        var current = (await rig.Documents().GetAsync(doc.DocumentId, None)).Value!;
        var v2 = current.Versions!.Single(v => v.VersionNumber == 2);
        Assert.True((await rig.Documents().PublishAsync(doc.DocumentId, v2.VersionId, current.Revision, None)).Succeeded);

        var versions = await ctx.Db.KnowledgeDocumentVersions.AsNoTracking().ToListAsync();
        Assert.Equal(VersionStatuses.Superseded, versions.Single(v => v.VersionNumber == 1).Status);
        Assert.Equal(v2.VersionId, Assert.Single(await new KnowledgeUnitOfWork(ctx.Db).GetRetrievableVersionsAsync(None)).VersionId);
    }

    [Fact]
    public async Task Chat_answers_with_citations_from_published_versions_only()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        // A second document that is only ready for review must not be offered to retrieval.
        var draftFile = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        await rig.Documents().RegisterAsync(admin, draftFile, "Draft", "FAQ", null, None);
        await rig.Ingestion().RunDueAsync(10, None);
        var user = await rig.AdminAsync();

        var answer = await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", "m1", None);
        Assert.True(answer.Succeeded, answer.Message);
        Assert.True(answer.Value!.Grounded);
        var citation = Assert.Single(answer.Value.Answer.Citations);
        Assert.Equal(doc.DocumentId, citation.DocumentId);
        Assert.Equal("Chính sách bảo hành", citation.DocumentTitle);
        Assert.Equal(doc.Versions![0].VersionId, Assert.Single(rig.Rag.LastAnswerRequest!.AllowedVersions).VersionId);
        Assert.False(string.IsNullOrWhiteSpace(answer.Value.Notice));

        var session = await rig.Chat().GetSessionAsync(user, answer.Value.SessionId, None);
        Assert.Equal(2, session.Value!.Messages.Count);
        Assert.Single(await rig.Chat().ListSessionsAsync(user, None));
        // Another user cannot read the session.
        Assert.Equal(KnowledgeErrorCodes.SessionNotFound, (await rig.Chat().GetSessionAsync(admin, answer.Value.SessionId, None)).ErrorCode);
        Assert.Equal(KnowledgeErrorCodes.SessionNotFound, (await rig.Chat().AskAsync(admin, answer.Value.SessionId, "Hi", null, None)).ErrorCode);
    }

    [Fact]
    public async Task Without_published_documents_the_assistant_says_it_does_not_know_and_never_calls_the_model()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var user = await rig.AdminAsync();
        var answer = await rig.Chat().AskAsync(user, null, "Giá điện mặt trời?", null, None);
        Assert.True(answer.Succeeded);
        Assert.False(answer.Value!.Grounded);
        Assert.Equal(ChatService.InsufficientAnswer, answer.Value.Answer.Content);
        Assert.Equal(ChatMessageStatuses.InsufficientContext, answer.Value.Answer.Status);
        Assert.Equal(0, rig.Rag.AnswerCalls);
    }

    [Fact]
    public async Task Ungrounded_or_uncited_model_output_is_replaced_by_the_insufficient_context_answer()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.PublishedAsync(admin);
        rig.Rag.Answer = _ => new RagAnswerResult(true, null, "Chắc là 25 năm.", true, [], 500, 30, "fake", 1, 1);

        var answer = await rig.Chat().AskAsync(admin, null, "Bảo hành inverter?", null, None);
        Assert.Equal(ChatService.InsufficientAnswer, answer.Value!.Answer.Content);
        Assert.Empty(answer.Value.Answer.Citations);
    }

    [Fact]
    public async Task A_document_unpublished_during_generation_is_not_cited()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        var doc = await rig.PublishedAsync(admin);
        rig.Rag.DuringAnswer = async () =>
        {
            Assert.True((await rig.Documents().UnpublishAsync(doc.DocumentId, doc.Revision, None)).Succeeded);
        };

        var answer = await rig.Chat().AskAsync(admin, null, "Tấm pin bảo hành bao lâu?", null, None);
        Assert.False(answer.Value!.Grounded);
        Assert.Empty(answer.Value.Answer.Citations);
        Assert.Equal(0, await ctx.Db.ChatCitations.CountAsync());
    }

    [Fact]
    public async Task Repeated_client_message_ids_return_the_stored_answer_without_paying_again()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.PublishedAsync(admin);
        var first = (await rig.Chat().AskAsync(admin, null, "Câu 1", "a", None)).Value!;
        var second = (await rig.Chat().AskAsync(admin, first.SessionId, "Câu 2", "b", None)).Value!;
        var retry = (await rig.Chat().AskAsync(admin, first.SessionId, "Câu 2", "b", None)).Value!;

        Assert.Equal(second.Answer.MessageId, retry.Answer.MessageId);
        Assert.Equal(2, rig.Rag.AnswerCalls);
        // The follow-up carried the earlier turns as history.
        Assert.Equal(2, rig.Rag.LastAnswerRequest!.History.Count);
        var usage = await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == admin);
        Assert.Equal(2, usage.Requests);
    }

    [Fact]
    public async Task Daily_quotas_and_the_concurrency_cap_are_enforced_and_reservations_released()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Options.DailyRequestsPerUser = 2;
        var admin = await rig.AdminAsync();
        await rig.PublishedAsync(admin);

        Assert.True((await rig.Chat().AskAsync(admin, null, "1", null, None)).Succeeded);
        Assert.True((await rig.Chat().AskAsync(admin, null, "2", null, None)).Succeeded);
        Assert.Equal(KnowledgeErrorCodes.QuotaExceeded, (await rig.Chat().AskAsync(admin, null, "3", null, None)).ErrorCode);
        var usage = await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == admin);
        Assert.Equal(0, await ctx.Db.RagUsageReservations.CountAsync());
        Assert.Equal(2 * 900, usage.InputTokens);
        Assert.Equal(2 * 120, usage.OutputTokens);
        var global = await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == Guid.Empty);
        Assert.Equal(2 * 900, global.InputTokens);

        // A new day resets the allowance.
        ctx.Clock.Now += TimeSpan.FromDays(1);
        Assert.True((await rig.Chat().AskAsync(admin, null, "4", null, None)).Succeeded);

        // Concurrency: with one generation in flight the second is refused.
        rig.Options.MaxConcurrentGenerationsPerUser = 1;
        var uow = new KnowledgeUnitOfWork(ctx.Db);
        var today = DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime);
        Assert.True((await uow.TryReserveUsageAsync(admin, today, 100, rig.Options, ctx.Clock.GetUtcNow(), ctx.Clock.GetUtcNow().AddMinutes(3), None)).Reserved);
        Assert.Equal(KnowledgeErrorCodes.TooManyConcurrent, (await rig.Chat().AskAsync(admin, null, "5", null, None)).ErrorCode);

        // Global token ceiling.
        var someone = await rig.AdminAsync();
        rig.Options.GlobalDailyTokens = 10;
        Assert.Equal(KnowledgeErrorCodes.QuotaExceeded, (await rig.Chat().AskAsync(someone, null, "6", null, None)).ErrorCode);
        var refused = await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == someone);
        Assert.Equal(0, refused.Requests);
        Assert.Equal(0, await ctx.Db.RagUsageReservations.CountAsync(r => r.UserId == someone));
    }

    [Fact]
    public async Task Service_outage_and_oversized_questions_fail_cleanly_and_release_the_reservation()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var admin = await rig.AdminAsync();
        await rig.PublishedAsync(admin);
        rig.Rag.Unavailable = true;

        Assert.Equal(KnowledgeErrorCodes.ServiceUnavailable, (await rig.Chat().AskAsync(admin, null, "Hello", null, None)).ErrorCode);
        Assert.Equal(KnowledgeErrorCodes.MessageTooLong, (await rig.Chat().AskAsync(admin, null, new string('a', rig.Options.MaxQuestionCharacters + 1), null, None)).ErrorCode);
        var usage = await ctx.Db.RagUsageDaily.AsNoTracking().SingleAsync(u => u.UserId == admin);
        Assert.Equal(0, await ctx.Db.RagUsageReservations.CountAsync());
        Assert.Equal(0, await ctx.Db.ChatMessages.CountAsync());
    }

    [Fact]
    public void Rag_requests_are_signed_with_hmac_over_timestamp_and_body()
    {
        var a = SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient.Sign("secret", "1700000000", "{\"a\":1}");
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient.Sign("secret", "1700000001", "{\"a\":1}"));
        Assert.NotEqual(a, SmartSolar.Infrastructure.ExternalProviders.Rag.RagServiceClient.Sign("other", "1700000000", "{\"a\":1}"));
        // Same vector as the Python service test (tests/test_security.py).
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData("secret"u8.ToArray(), "1700000000.{\"a\":1}"u8.ToArray())).ToLowerInvariant(), a);
    }
}
