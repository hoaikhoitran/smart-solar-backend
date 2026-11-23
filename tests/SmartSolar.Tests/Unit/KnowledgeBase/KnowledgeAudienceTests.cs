using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// Shared publication scope (2026-11-23): publishing is an explicit, audited Admin approval for ALL signed-in roles; every role retrieves the
/// same approved published versions; a document published under the former audience policy stays unretrievable for everyone until an Admin
/// re-confirms it (legacy-content gate); legacy answers keep their withheld rule; citations disappear once their version is no longer retrievable.
/// </summary>
public sealed class KnowledgeAudienceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig(InstallationWorkflowTestContext ctx)
    {
        public InstallationWorkflowTestContext Ctx { get; } = ctx;
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32) };
        public KnowledgeDocumentHandlers Documents() => new(new KnowledgeUnitOfWork(Ctx.Db), Ctx.Files(), Options, Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        public ChatService Chat() => new(new KnowledgeUnitOfWork(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<ChatService>.Instance);

        public async Task<Guid> AdminAsync() => (await Ctx.Scheduling.SeedTechnicianAsync($"Admin {Guid.NewGuid():N}", null, withRole: false)).Id;

        public async Task<(Guid Admin, KnowledgeDocumentView Ready)> ReadyAsync(string title = "Bảo hành")
        {
            var admin = await AdminAsync();
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = (await Documents().RegisterAsync(admin, file, title, "warranty", null, None)).Value!;
            await new KnowledgeIngestionService(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), Rag, Options, Ctx.Clock,
                NullLogger<KnowledgeIngestionService>.Instance).RunDueAsync(10, None);
            return (admin, (await Documents().GetAsync(registered.DocumentId, None)).Value!);
        }

        public async Task<(Guid Admin, KnowledgeDocumentView Doc)> PublishedAsync(string title = "Bảo hành")
        {
            var (admin, ready) = await ReadyAsync(title);
            return (admin, (await Documents().PublishAsync(admin, ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, true, "reviewed", None)).Value!);
        }

        /// <summary>Simulates a document published under the former audience policy (no shared-scope approval), INTERNAL by default.</summary>
        public async Task<KnowledgeDocumentView> LegacyPublishedAsync(string audience = KnowledgeAudiences.Internal)
        {
            var (_, doc) = await PublishedAsync("Giá nội bộ");
            await Ctx.Db.KnowledgeDocumentVersions.Where(v => v.Id == doc.PublishedVersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.SharedScopeApprovedAt, (DateTimeOffset?)null).SetProperty(v => v.SharedScopeApprovedByUserId, (Guid?)null));
            await Ctx.Db.KnowledgeDocuments.Where(d => d.Id == doc.DocumentId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Audience, audience));
            await Ctx.Db.KnowledgePublicationApprovals.Where(a => a.DocumentId == doc.DocumentId).ExecuteDeleteAsync();   // no approval existed then
            Ctx.Db.ChangeTracker.Clear();
            return (await Documents().GetAsync(doc.DocumentId, None)).Value!;
        }
    }

    [Fact]
    public async Task Publishing_requires_the_explicit_all_roles_confirmation_and_is_audited()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (admin, ready) = await rig.ReadyAsync();
        var uow = new KnowledgeUnitOfWork(ctx.Db);

        var refused = await rig.Documents().PublishAsync(admin, ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, false, null, None);
        Assert.Equal(KnowledgeErrorCodes.PublishConfirmationRequired, refused.ErrorCode);
        Assert.Empty(await uow.GetRetrievableVersionsAsync(None));
        Assert.Equal(CommonErrorCodes.ValidationFailed,
            (await rig.Documents().PublishAsync(admin, ready.DocumentId, ready.Versions[0].VersionId, ready.Revision, true, new string('x', 1001), None)).ErrorCode);

        var published = (await rig.Documents().PublishAsync(admin, ready.DocumentId, ready.Versions[0].VersionId, ready.Revision, true, "Customer-safe FAQ", None)).Value!;
        Assert.Equal((SharedVisibilityStatuses.Approved, admin), (published.SharedVisibility!.Status, published.SharedVisibility.ApprovedByUserId!.Value));
        Assert.Equal(KnowledgeAudiences.Shared, published.SharedVisibility.Scope);
        var approval = Assert.Single(published.PublicationApprovals!);
        Assert.Equal((KnowledgePublicationActions.ApprovedAllSignedInRoles, admin, "Customer-safe FAQ"), (approval.Action, approval.ActorUserId!.Value, approval.Note));
        Assert.Single(await uow.GetRetrievableVersionsAsync(None));
        // Idempotent: publishing the approved live version again changes nothing and adds no audit row.
        var again = (await rig.Documents().PublishAsync(admin, ready.DocumentId, ready.Versions[0].VersionId, published.Revision, true, null, None)).Value!;
        Assert.Single(again.PublicationApprovals!);
    }

    [Fact]
    public async Task Every_role_retrieves_exactly_the_same_published_versions_and_unapproved_states_are_never_retrievable()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (_, doc) = await rig.PublishedAsync();
        await rig.ReadyAsync("Bản nháp chưa duyệt");                       // READY_FOR_REVIEW, never retrievable
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;
        var staff = (await ctx.Scheduling.Inner.SeedUserAsync("Technician")).Id;

        var asCustomer = (await rig.Chat().AskAsync(customer, null, "Bảo hành bao lâu?", null, None, KnowledgeAudience.Customer, userRole: "CUSTOMER")).Value!;
        var customerAllowed = rig.Rag.LastAnswerRequest!.AllowedVersions.Select(v => v.VersionId).ToList();
        var asStaff = (await rig.Chat().AskAsync(staff, null, "Bảo hành bao lâu?", null, None, KnowledgeAudience.Staff, userRole: "TECHNICIAN")).Value!;
        var staffAllowed = rig.Rag.LastAnswerRequest!.AllowedVersions.Select(v => v.VersionId).ToList();

        Assert.Equal(new[] { doc.PublishedVersionId!.Value }, customerAllowed);
        Assert.Equal(customerAllowed, staffAllowed);
        Assert.True(asCustomer.Grounded && asStaff.Grounded);
        Assert.Equal(asCustomer.Answer.Citations.Single().VersionId, asStaff.Answer.Citations.Single().VersionId);
        Assert.Equal(KnowledgeAudiences.Shared, (await ctx.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == asCustomer.Answer.MessageId)).RetrievalAudience);
    }

    [Fact]
    public async Task A_legacy_internal_document_is_retrievable_by_nobody_until_an_admin_reconfirms_it()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var legacy = await rig.LegacyPublishedAsync();
        var admin = await rig.AdminAsync();
        var uow = new KnowledgeUnitOfWork(ctx.Db);
        var staff = (await ctx.Scheduling.Inner.SeedUserAsync("Manager")).Id;

        Assert.Equal(SharedVisibilityStatuses.PendingReview, legacy.SharedVisibility!.Status);
        Assert.Empty(await uow.GetRetrievableVersionsAsync(None));
        var review = await rig.Documents().SharedScopeReviewAsync(None);
        Assert.Equal((1, 1), (review.PendingCount, review.PendingInternalCount));
        Assert.Equal((legacy.DocumentId, legacy.PublishedVersionId!.Value, KnowledgeAudiences.Internal),
            (review.Items[0].DocumentId, review.Items[0].PublishedVersionId, review.Items[0].LegacyAudience));

        // Not even staff retrieve it now; the honest result is "insufficient", not an answer from unapproved content.
        var answer = (await rig.Chat().AskAsync(staff, null, "Giá vốn?", null, None, KnowledgeAudience.Staff)).Value!;
        Assert.False(answer.Grounded);
        Assert.Equal(InsufficientReasons.NoPublishedDocuments, answer.InsufficientReason);
        Assert.Equal(0, rig.Rag.AnswerCalls);

        // Without confirmation nothing changes; with it the SAME live version is approved (status unchanged) and audited.
        Assert.Equal(KnowledgeErrorCodes.PublishConfirmationRequired,
            (await rig.Documents().PublishAsync(admin, legacy.DocumentId, legacy.PublishedVersionId.Value, legacy.Revision, false, null, None)).ErrorCode);
        var confirmed = (await rig.Documents().PublishAsync(admin, legacy.DocumentId, legacy.PublishedVersionId.Value, legacy.Revision, true, "Reviewed: no supplier prices", None)).Value!;
        Assert.Equal((DocumentStatuses.Published, legacy.PublishedVersionId, SharedVisibilityStatuses.Approved),
            (confirmed.Status, confirmed.PublishedVersionId, confirmed.SharedVisibility!.Status));
        Assert.Equal(KnowledgeAudiences.Internal, Assert.Single(confirmed.PublicationApprovals!).LegacyAudience);
        Assert.Single(await uow.GetRetrievableVersionsAsync(None));
        Assert.Equal(0, (await rig.Documents().SharedScopeReviewAsync(None)).PendingCount);
    }

    [Fact]
    public async Task Unpublishing_a_legacy_document_is_the_safe_withdrawal_and_an_archived_or_superseded_version_cannot_be_confirmed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var legacy = await rig.LegacyPublishedAsync();
        var admin = await rig.AdminAsync();

        var withdrawn = (await rig.Documents().UnpublishAsync(legacy.DocumentId, legacy.Revision, None)).Value!;
        Assert.Equal(0, (await rig.Documents().SharedScopeReviewAsync(None)).PendingCount);
        Assert.Equal(SharedVisibilityStatuses.NotPublished, withdrawn.SharedVisibility!.Status);
        Assert.Equal(VersionStatuses.Withdrawn, withdrawn.Versions!.Single().Status);          // kept, nothing deleted
        var archived = (await rig.Documents().ArchiveAsync(legacy.DocumentId, withdrawn.Revision, None)).Value!;
        Assert.Equal(KnowledgeErrorCodes.InvalidState,
            (await rig.Documents().PublishAsync(admin, legacy.DocumentId, legacy.PublishedVersionId!.Value, archived.Revision, true, null, None)).ErrorCode);
    }

    [Fact]
    public async Task A_version_unpublished_during_generation_is_neither_output_nor_cited_and_the_reason_says_so()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (_, doc) = await rig.PublishedAsync();
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;
        rig.Rag.DuringAnswer = async () =>
        {
            await rig.Documents().UnpublishAsync(doc.DocumentId, doc.Revision, None);
            ctx.Db.ChangeTracker.Clear();
        };

        var answer = (await rig.Chat().AskAsync(customer, null, "Bảo hành bao lâu?", null, None)).Value!;
        Assert.False(answer.Grounded);
        Assert.Equal(ChatService.InsufficientAnswer, answer.Answer.Content);
        Assert.Empty(answer.Answer.Citations);
        Assert.Equal(InsufficientReasons.CitationsRevoked, answer.InsufficientReason);
    }

    [Fact]
    public async Task A_shared_answer_stays_readable_by_its_owner_but_its_citation_disappears_once_the_version_is_unpublished()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (_, doc) = await rig.PublishedAsync();
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;
        var other = (await ctx.Scheduling.Inner.SeedUserAsync("Other customer")).Id;
        var answer = (await rig.Chat().AskAsync(customer, null, "Bảo hành bao lâu?", null, None)).Value!;
        Assert.Single(answer.Answer.Citations);

        await rig.Documents().UnpublishAsync(doc.DocumentId, doc.Revision, None);
        ctx.Db.ChangeTracker.Clear();
        var session = (await rig.Chat().GetSessionAsync(customer, answer.SessionId, None)).Value!;
        var stored = session.Messages.Single(m => m.Role == ChatRoles.Assistant);
        Assert.False(stored.Withheld);
        Assert.Empty(stored.Citations);
        // Sharing documents never shares sessions.
        Assert.Equal(KnowledgeErrorCodes.SessionNotFound, (await rig.Chat().GetSessionAsync(other, answer.SessionId, None)).ErrorCode);
    }

    [Fact]
    public async Task A_legacy_answer_is_withheld_from_the_customer_and_never_fed_back_to_the_model_but_staff_still_read_it()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        await rig.PublishedAsync();
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;

        // A session answered before audiences existed (retrieval_audience NULL) and one retrieved for staff (INTERNAL): their text may come
        // from internal documents, so a customer never reads it, even now that documents are shared.
        var session = new ChatSession { Id = Guid.NewGuid(), UserId = customer, Title = "Old", CreatedAt = ctx.Clock.GetUtcNow(), UpdatedAt = ctx.Clock.GetUtcNow() };
        var question = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.User, Content = "Giá vốn?", Status = ChatMessageStatuses.Completed,
            CreatedAt = ctx.Clock.GetUtcNow() };
        var legacy = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.Assistant, Content = "Giá vốn nội bộ là 1.2 triệu.",
            Status = ChatMessageStatuses.Completed, ReplyToMessageId = question.Id, CreatedAt = ctx.Clock.GetUtcNow().AddTicks(1) };
        var question2 = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.User, Content = "Chiết khấu đại lý?", Status = ChatMessageStatuses.Completed,
            CreatedAt = ctx.Clock.GetUtcNow().AddTicks(2) };
        var staffAnswer = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.Assistant, Content = "Chiết khấu đại lý 18%.",
            Status = ChatMessageStatuses.Completed, ReplyToMessageId = question2.Id, CreatedAt = ctx.Clock.GetUtcNow().AddTicks(3),
            RetrievalAudience = KnowledgeAudiences.Internal };
        ctx.Db.AddRange(session, question, legacy, question2, staffAnswer);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var asCustomer = (await rig.Chat().GetSessionAsync(customer, session.Id, None)).Value!;
        Assert.All(asCustomer.Messages.Where(m => m.Role == ChatRoles.Assistant),
            m => Assert.Equal((true, ChatService.WithheldAnswer, 0), (m.Withheld, m.Content, m.Citations.Count)));
        Assert.Equal("Giá vốn?", asCustomer.Messages.First(m => m.Role == ChatRoles.User).Content);   // the customer's own words stay
        var asStaff = (await rig.Chat().GetSessionAsync(customer, session.Id, None, KnowledgeAudience.Staff)).Value!;
        Assert.Equal("Giá vốn nội bộ là 1.2 triệu.", asStaff.Messages.First(m => m.Role == ChatRoles.Assistant).Content);
        Assert.Equal("Giá vốn nội bộ là 1.2 triệu.", (await ctx.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == legacy.Id)).Content);   // kept

        // A follow-up question: no legacy answer is sent to the model; the new answer comes from the shared scope and is readable.
        var answer = (await rig.Chat().AskAsync(customer, session.Id, "Bảo hành bao lâu?", null, None)).Value!;
        Assert.DoesNotContain(rig.Rag.LastAnswerRequest!.History, t => t.Content.Contains("1.2 triệu") || t.Content.Contains("18%"));
        Assert.False(answer.Answer.Withheld);
        Assert.Equal(KnowledgeAudiences.Shared, (await ctx.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == answer.Answer.MessageId)).RetrievalAudience);
    }
}
