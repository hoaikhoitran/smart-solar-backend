using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// MIGRATION 20261123140208_SharedKnowledgePublicationScope on a disposable PostgreSQL database: the legacy-content gate of the cutover
/// (only documents already readable by every role are carried over, INTERNAL ones stay unretrievable until reviewed), the audit rows, the
/// widened check, and a rollback that keeps every stored answer safe for customers. Nothing is deleted.
/// </summary>
public sealed class PostgresSharedPublicationCutoverTests
{
    private const string Migration = "20261123140208_SharedKnowledgePublicationScope";
    private const string Previous = "20261118202100_PaymentPlan3070PaperSignatureCollection";
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Cutover_carries_over_only_customer_visible_documents_and_rollback_keeps_answers_safe()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32) };
        var rag = new FakeRagServiceClient();
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("KB admin")).Id;
        var customer = (await ctx.Scheduling.Inner.SeedUserAsync("Customer")).Id;
        var documents = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(ctx.Db), ctx.Files(), options, ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        async Task<Guid> PublishedAsync(string title)
        {
            var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var doc = (await documents.RegisterAsync(admin, file, title, "WARRANTY", null, None)).Value!;
            await new KnowledgeIngestionService(new KnowledgeUnitOfWork(ctx.Db), new FileStore(ctx.Db), rag, options, ctx.Clock,
                NullLogger<KnowledgeIngestionService>.Instance).RunDueAsync(10, None);
            var ready = (await documents.GetAsync(doc.DocumentId, None)).Value!;
            Assert.True((await documents.PublishAsync(admin, doc.DocumentId, ready.Versions![0].VersionId, ready.Revision, true, null, None)).Succeeded);
            return doc.DocumentId;
        }
        var customerDoc = await PublishedAsync("Bảo hành");
        var internalDoc = await PublishedAsync("Giá nội bộ");
        var answer = (await new ChatService(new KnowledgeUnitOfWork(ctx.Db), rag, options, ctx.Clock, NullLogger<ChatService>.Instance)
            .AskAsync(customer, null, "Bảo hành bao lâu?", "q-1", None)).Value!;
        ctx.Db.ChangeTracker.Clear();

        // Rollback (Down): the shared-scope answer becomes INTERNAL (withheld from customers by the former build), nothing is deleted.
        await using (var db = database.NewContext())
            await db.GetService<IMigrator>().MigrateAsync(Previous);
        await using (var db = database.NewContext())
        {
            Assert.Equal("INTERNAL", await db.Database.SqlQueryRaw<string>(
                $"SELECT retrieval_audience AS \"Value\" FROM chat_message WHERE id = '{answer.Answer.MessageId}'").SingleAsync());
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('knowledge_publication_approval') IS NOT NULL AS \"Value\"").SingleAsync());
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM knowledge_document WHERE status = 'PUBLISHED'").SingleAsync());
            // The state before the cutover: one customer-visible document and one internal one, both PUBLISHED.
            await db.Database.ExecuteSqlRawAsync($"UPDATE knowledge_document SET audience = 'CUSTOMER' WHERE id = '{customerDoc}'");
            await db.Database.ExecuteSqlRawAsync($"UPDATE knowledge_document SET audience = 'INTERNAL' WHERE id = '{internalDoc}'");
        }

        // Up: only the CUSTOMER document is carried over (audited, no actor); the INTERNAL one stays published but unretrievable.
        await using (var db = database.NewContext())
        {
            await db.Database.MigrateAsync();
            Assert.Equal(Migration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        await using (var db = database.NewContext())
        {
            var uow = new KnowledgeUnitOfWork(db);
            var retrievable = await uow.GetRetrievableVersionsAsync(None);
            Assert.Equal(customerDoc, Assert.Single(retrievable).DocumentId);
            var pending = Assert.Single(await uow.GetSharedScopeReviewAsync(None));
            Assert.Equal((internalDoc, KnowledgeAudiences.Internal, DocumentStatuses.Published), (pending.DocumentId, pending.LegacyAudience, pending.Status));
            var audit = Assert.Single(await db.KnowledgePublicationApprovals.AsNoTracking().ToListAsync());
            Assert.Equal((customerDoc, KnowledgePublicationActions.MigratedFromCustomerAudience, (Guid?)null, KnowledgeAudiences.Customer),
                (audit.DocumentId, audit.Action, audit.ActorUserId, audit.LegacyAudience));
            // The check accepts the new scope and still refuses unknown values; an approval without actor is only valid for the migration.
            await db.Database.ExecuteSqlRawAsync($"UPDATE chat_message SET retrieval_audience = 'ALL_SIGNED_IN_ROLES' WHERE id = '{answer.Answer.MessageId}'");
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                $"UPDATE chat_message SET retrieval_audience = 'PUBLIC' WHERE id = '{answer.Answer.MessageId}'"));
        }
        await using (var db = database.NewContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO knowledge_publication_approval (id, document_id, version_id, action, legacy_audience, actor_user_id, created_at) "
                + $"SELECT gen_random_uuid(), id, published_version_id, 'APPROVED_ALL_SIGNED_IN_ROLES', audience, NULL, now() FROM knowledge_document WHERE id = '{internalDoc}'"));

            // The Admin re-confirms the INTERNAL document after review: retrievable by every role, audited with the Admin as actor.
            var handlers = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(db), ctx.Files(), options, ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
            var view = (await handlers.GetAsync(internalDoc, None)).Value!;
            Assert.Equal(SharedVisibilityStatuses.PendingReview, view.SharedVisibility!.Status);
            var confirmed = (await handlers.PublishAsync(admin, internalDoc, view.PublishedVersionId!.Value, view.Revision, true, "Reviewed after cutover", None)).Value!;
            Assert.Equal(SharedVisibilityStatuses.Approved, confirmed.SharedVisibility!.Status);
            Assert.Equal(2, (await new KnowledgeUnitOfWork(db).GetRetrievableVersionsAsync(None)).Count);
        }
    }
}
