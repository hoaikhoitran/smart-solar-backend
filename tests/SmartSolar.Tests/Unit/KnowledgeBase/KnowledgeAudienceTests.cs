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
/// M-2 / D-07: the persisted document audience is authoritative and audited; every document starts INTERNAL; a customer never reads answer
/// text that was not retrieved for customers (legacy answers, or answers built from a document reclassified INTERNAL since), neither in
/// the session view nor through the history passed back to the model. The stored text is kept for review.
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

        public async Task<(Guid Admin, KnowledgeDocumentView Doc)> PublishedAsync()
        {
            var admin = (await Ctx.Scheduling.SeedTechnicianAsync($"Admin {Guid.NewGuid():N}", null, withRole: false)).Id;
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = (await Documents().RegisterAsync(admin, file, "Bảo hành", "warranty", null, None)).Value!;
            await new KnowledgeIngestionService(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), Rag, Options, Ctx.Clock,
                NullLogger<KnowledgeIngestionService>.Instance).RunDueAsync(10, None);
            var ready = (await Documents().GetAsync(registered.DocumentId, None)).Value!;
            return (admin, (await Documents().PublishAsync(ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, None)).Value!);
        }
    }

    [Fact]
    public async Task Documents_start_internal_and_only_an_audited_valid_current_revision_change_makes_them_customer_visible()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (admin, doc) = await rig.PublishedAsync();
        var uow = new KnowledgeUnitOfWork(ctx.Db);
        Assert.Equal(KnowledgeAudiences.Internal, doc.Audience);
        Assert.Empty(await uow.GetCustomerRetrievableVersionsAsync(None));

        var docs = rig.Documents();
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await docs.SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "PUBLIC", "x", None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await docs.SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "CUSTOMER", " ", None)).ErrorCode);
        Assert.Equal(KnowledgeErrorCodes.AudienceUnchanged, (await docs.SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "INTERNAL", "x", None)).ErrorCode);
        Assert.Equal(KnowledgeErrorCodes.DocumentNotFound, (await docs.SetAudienceAsync(admin, Guid.NewGuid(), 0, "CUSTOMER", "x", None)).ErrorCode);

        // Two Admins change it from the same revision: the second is refused as stale, so the audit trail never misses a step.
        var first = await docs.SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "customer", "FAQ reviewed for customers", None);
        var second = await rig.Documents().SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "CUSTOMER", "duplicate", None);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(KnowledgeErrorCodes.StaleRevision, second.ErrorCode);
        Assert.Single(await uow.GetCustomerRetrievableVersionsAsync(None));

        var back = (await docs.SetAudienceAsync(admin, doc.DocumentId, first.Value!.Revision, "INTERNAL", "Supplier terms found", None)).Value!;
        Assert.Empty(await uow.GetCustomerRetrievableVersionsAsync(None));   // revoked immediately
        Assert.Equal(new[] { ("INTERNAL", "CUSTOMER", "FAQ reviewed for customers"), ("CUSTOMER", "INTERNAL", "Supplier terms found") },
            back.AudienceChanges!.Select(c => (c.FromAudience, c.ToAudience, c.Reason)));
        Assert.All(back.AudienceChanges!, c => Assert.Equal(admin, c.ActorUserId));
        Assert.Equal(2, await ctx.Db.KnowledgeDocumentAudienceChanges.CountAsync(c => c.DocumentId == doc.DocumentId));
    }

    [Fact]
    public async Task A_legacy_answer_is_withheld_from_the_customer_and_never_fed_back_to_the_model_but_staff_still_read_it()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (admin, doc) = await rig.PublishedAsync();
        await rig.Documents().SetAudienceAsync(admin, doc.DocumentId, doc.Revision, "CUSTOMER", "Customer FAQ", None);
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;

        // A session answered before audiences existed (retrieval_audience NULL): its text may come from any published document.
        var session = new ChatSession { Id = Guid.NewGuid(), UserId = customer, Title = "Old", CreatedAt = ctx.Clock.GetUtcNow(), UpdatedAt = ctx.Clock.GetUtcNow() };
        var question = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.User, Content = "Giá vốn?", Status = ChatMessageStatuses.Completed,
            CreatedAt = ctx.Clock.GetUtcNow() };
        var legacy = new ChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, Role = ChatRoles.Assistant, Content = "Giá vốn nội bộ là 1.2 triệu.",
            Status = ChatMessageStatuses.Completed, ReplyToMessageId = question.Id, CreatedAt = ctx.Clock.GetUtcNow().AddTicks(1) };
        ctx.Db.AddRange(session, question, legacy);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var asCustomer = (await rig.Chat().GetSessionAsync(customer, session.Id, None)).Value!;
        var withheld = asCustomer.Messages.Single(m => m.Role == ChatRoles.Assistant);
        Assert.Equal((true, ChatService.WithheldAnswer, 0), (withheld.Withheld, withheld.Content, withheld.Citations.Count));
        Assert.Equal("Giá vốn?", asCustomer.Messages.Single(m => m.Role == ChatRoles.User).Content);   // the customer's own words stay
        var asStaff = (await rig.Chat().GetSessionAsync(customer, session.Id, None, KnowledgeAudience.Staff)).Value!;
        Assert.Equal("Giá vốn nội bộ là 1.2 triệu.", asStaff.Messages.Single(m => m.Role == ChatRoles.Assistant).Content);
        Assert.Equal("Giá vốn nội bộ là 1.2 triệu.", (await ctx.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == legacy.Id)).Content);   // kept

        // A follow-up question in the same session: the legacy answer is not part of the history sent to the model; the new answer is
        // stored as retrieved for customers and is readable.
        var answer = (await rig.Chat().AskAsync(customer, session.Id, "Bảo hành bao lâu?", null, None)).Value!;
        Assert.DoesNotContain(rig.Rag.LastAnswerRequest!.History, t => t.Content.Contains("1.2 triệu"));
        Assert.False(answer.Answer.Withheld);
        Assert.Equal(KnowledgeAudiences.Customer, (await ctx.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Id == answer.Answer.MessageId)).RetrievalAudience);
    }
}
