using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>Knowledge base on a disposable local PostgreSQL database: ingestion leases and the cost ledger under concurrency.</summary>
public sealed class PostgresKnowledgeBaseConcurrencyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Two_ingestion_workers_process_a_job_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var rag = new FakeRagServiceClient();
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32), DailyRequestsPerUser = 5, MaxConcurrentGenerationsPerUser = 100 };
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("KB admin")).Id;
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var documents = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(ctx.Db), ctx.Files(), options, ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        Assert.True((await documents.RegisterAsync(admin, file, "Warranty", "WARRANTY", null, None)).Succeeded);
        var jobId = (await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync()).Id;

        var ran = await RaceAsync(database, 4, (db, _) =>
            new KnowledgeIngestionService(new KnowledgeUnitOfWork(db), new FileStore(db), rag, options, ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance).RunAsync(jobId, None));
        Assert.Single(ran, r => r);
        Assert.Equal(1, rag.IngestCalls);

    }

    private static RagOptions Limits() => new()
    {
        Enabled = true, ServiceSharedSecret = new string('x', 32), DailyRequestsPerUser = 100, DailyTokensPerUser = 1_000_000,
        GlobalDailyTokens = 10_000_000, MaxConcurrentGenerationsPerUser = 100,
    };

    [PostgresFact]
    public async Task Concurrent_reservations_of_one_user_respect_the_daily_request_allowance()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = Limits();
        options.DailyRequestsPerUser = 5;
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Chat user")).Id;
        var now = ctx.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var results = await RaceAsync(database, 20, (db, _) => new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 1000, options, now, now.AddMinutes(3), None));

        Assert.Equal(5, results.Count(r => r.Reserved));
        Assert.All(results.Where(r => !r.Reserved), r => Assert.Equal("USER_QUOTA", r.Limit));
        await using var check = database.NewContext();
        Assert.Equal(5, (await check.RagUsageDaily.SingleAsync(u => u.UserId == user)).Requests);
        Assert.Equal(5, await check.RagUsageReservations.CountAsync(r => r.UserId == user));
        Assert.Equal(5, (await check.RagUsageDaily.SingleAsync(u => u.UserId == Guid.Empty)).Requests);
    }

    [PostgresFact]
    public async Task The_global_token_cap_holds_across_users_and_refusals_leave_no_partial_charge()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = Limits();
        options.GlobalDailyTokens = 5_000;
        var users = new List<Guid>();
        for (var i = 0; i < 20; i++) users.Add((await ctx.Scheduling.Inner.SeedUserAsync($"User {i}")).Id);
        var now = ctx.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var results = await RaceAsync(database, 20, (db, i) => new KnowledgeUnitOfWork(db).TryReserveUsageAsync(users[i], today, 1000, options, now, now.AddMinutes(3), None));

        Assert.Equal(5, results.Count(r => r.Reserved));
        Assert.All(results.Where(r => !r.Reserved), r => Assert.Equal("GLOBAL_QUOTA", r.Limit));
        await using var check = database.NewContext();
        var refused = users.Where((_, i) => !results[i].Reserved).ToList();
        Assert.Equal(0, await check.RagUsageDaily.Where(u => refused.Contains(u.UserId)).SumAsync(u => u.Requests));
        Assert.Equal(0, await check.RagUsageReservations.CountAsync(r => refused.Contains(r.UserId)));
        Assert.Equal(5, (await check.RagUsageDaily.SingleAsync(u => u.UserId == Guid.Empty)).Requests);
        Assert.Equal(5_000, await check.RagUsageReservations.SumAsync(r => r.ReservedTokens));
    }

    [PostgresFact]
    public async Task A_crashed_request_frees_its_slot_when_the_lease_expires_and_is_charged_at_its_estimate()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = Limits();
        options.MaxConcurrentGenerationsPerUser = 1;
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Crashing user")).Id;
        var now = ctx.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await using (var db = database.NewContext())
            Assert.True((await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 4000, options, now, now.AddMinutes(3), None)).Reserved);
        // The API instance dies here: the reservation is never completed.
        await using (var db = database.NewContext())
            Assert.Equal("CONCURRENCY", (await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 4000, options, now.AddMinutes(1), now.AddMinutes(4), None)).Limit);

        var later = now.AddMinutes(5);
        await using (var db = database.NewContext())
            Assert.True((await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 4000, options, later, later.AddMinutes(3), None)).Reserved);

        await using var check = database.NewContext();
        var row = await check.RagUsageDaily.SingleAsync(u => u.UserId == user);
        Assert.Equal(4000, row.EstimatedTokens);              // not forgiven, not reset
        Assert.Equal(2, row.Requests);
        Assert.Equal(1, await check.RagUsageReservations.CountAsync(r => r.UserId == user));
        Assert.Equal(4000, (await check.RagUsageDaily.SingleAsync(u => u.UserId == Guid.Empty)).EstimatedTokens);
    }

    [PostgresFact]
    public async Task Completing_a_reservation_is_idempotent_under_concurrency_and_after_expiry()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = Limits();
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Completing user")).Id;
        var now = ctx.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        Guid reservationId;
        await using (var db = database.NewContext())
            reservationId = (await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 3000, options, now, now.AddMinutes(3), None)).ReservationId!.Value;

        var completed = await RaceAsync(database, 6, (db, _) => new KnowledgeUnitOfWork(db).CompleteUsageAsync(reservationId, 900, 120, now.AddSeconds(5), None));

        Assert.Single(completed, c => c);
        await using var check = database.NewContext();
        var row = await check.RagUsageDaily.SingleAsync(u => u.UserId == user);
        Assert.Equal((900, 120, 0L), (row.InputTokens, row.OutputTokens, row.EstimatedTokens));

        // A second reservation expires and is converted before its late completion arrives: charged once.
        Guid late;
        await using (var db = database.NewContext())
            late = (await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 2000, options, now, now.AddMinutes(3), None)).ReservationId!.Value;
        await using (var db = database.NewContext())
            Assert.True((await new KnowledgeUnitOfWork(db).TryReserveUsageAsync(user, today, 10, options, now.AddMinutes(10), now.AddMinutes(13), None)).Reserved);
        await using (var db = database.NewContext())
            Assert.False(await new KnowledgeUnitOfWork(db).CompleteUsageAsync(late, 500, 50, now.AddMinutes(11), None));
        await using var after = database.NewContext();
        row = await after.RagUsageDaily.SingleAsync(u => u.UserId == user);
        Assert.Equal((900, 120, 2000L), (row.InputTokens, row.OutputTokens, row.EstimatedTokens));
    }

    [PostgresFact]
    public async Task Concurrent_questions_through_two_replicas_never_exceed_the_generation_cap_and_release_every_slot()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var options = Limits();
        options.MaxConcurrentGenerationsPerUser = 2;
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("KB admin")).Id;
        var rag = new FakeRagServiceClient();
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var documents = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(ctx.Db), ctx.Files(), options, ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        var doc = (await documents.RegisterAsync(admin, file, "Warranty", "WARRANTY", null, None)).Value!;
        await new KnowledgeIngestionService(new KnowledgeUnitOfWork(ctx.Db), new FileStore(ctx.Db), rag, options, ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance).RunDueAsync(5, None);
        var ready = (await documents.GetAsync(doc.DocumentId, None)).Value!;
        Assert.True((await documents.PublishAsync(doc.DocumentId, ready.Versions![0].VersionId, ready.Revision, None)).Succeeded);

        var inFlight = 0;
        var maxInFlight = 0;
        rag.DuringAnswer = async () =>
        {
            var current = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = maxInFlight) < current && Interlocked.CompareExchange(ref maxInFlight, current, seen) != seen) { }
            await Task.Delay(150);
            Interlocked.Decrement(ref inFlight);
        };
        var user = (await ctx.Scheduling.Inner.SeedUserAsync("Busy customer")).Id;

        // Ten simultaneous questions, each on its own DbContext (as two API replicas would be).
        var answers = await RaceAsync(database, 10, (db, i) =>
            new ChatService(new KnowledgeUnitOfWork(db), rag, options, ctx.Clock, NullLogger<ChatService>.Instance).AskAsync(user, null, $"Question {i}", null, None));

        Assert.True(maxInFlight <= 2, $"{maxInFlight} generations ran at once.");
        Assert.InRange(answers.Count(a => a.Succeeded), 1, 10);
        Assert.All(answers.Where(a => !a.Succeeded), a => Assert.Equal(KnowledgeErrorCodes.TooManyConcurrent, a.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(0, await check.RagUsageReservations.CountAsync(r => r.UserId == user));
        var row = await check.RagUsageDaily.SingleAsync(u => u.UserId == user);
        Assert.Equal(answers.Count(a => a.Succeeded), row.Requests);
        Assert.Equal(answers.Count(a => a.Succeeded) * 900L, row.InputTokens);
    }
}
