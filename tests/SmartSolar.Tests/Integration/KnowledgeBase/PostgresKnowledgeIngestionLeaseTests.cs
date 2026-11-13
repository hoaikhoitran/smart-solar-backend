using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// Ingestion leases on a disposable local PostgreSQL database. timestamptz keeps microseconds while .NET keeps 100 ns ticks, so
/// the clock here deliberately has sub-microsecond ticks (as the system clock does in production); the shared test clock is a
/// whole second, which hid the lease-precision bug.
/// </summary>
public sealed class PostgresKnowledgeIngestionLeaseTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Not a whole number of microseconds: 0.1234567 s past the second.</summary>
    private static readonly TimeSpan SubMicrosecond = TimeSpan.FromTicks(1_234_567);

    /// <summary>IngestTimeoutSeconds (300 by default) + 120 s.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(420);

    /// <summary>What timestamptz keeps of a .NET value (whole microseconds).</summary>
    private static DateTimeOffset Micro(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));

    private static RagOptions Options() => new() { Enabled = true, ServiceSharedSecret = new string('x', 32), MaxIngestAttempts = 3 };

    private static KnowledgeIngestionService Ingestion(AppDbContext db, IRagServiceClient rag, RagOptions options, TimeProvider clock)
        => new(new KnowledgeUnitOfWork(db), new FileStore(db), rag, options, clock, NullLogger<KnowledgeIngestionService>.Instance);

    private static async Task<(PostgresTestDatabase Database, InstallationWorkflowTestContext Ctx, Guid JobId)> RegisteredAsync(RagOptions options)
    {
        var (database, ctx) = await WorldAsync();
        ctx.Clock.Now = ctx.Clock.Now + SubMicrosecond;
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("KB admin")).Id;
        var file = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var documents = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(ctx.Db), ctx.Files(), options, ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        Assert.True((await documents.RegisterAsync(admin, file, "Warranty", "WARRANTY", null, None)).Succeeded);
        return (database, ctx, (await ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync()).Id);
    }

    [PostgresFact]
    public async Task A_ready_ingestion_completes_the_job_and_makes_the_version_ready_for_review()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var rag = new FakeRagServiceClient();

        await using (var db = database.NewContext())
            Assert.True(await Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, None));

        await using var check = database.NewContext();
        var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.Completed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Null(job.LockedUntil);
        Assert.Null(job.LastError);
        var version = await check.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        Assert.Equal(VersionStatuses.ReadyForReview, version.Status);
        Assert.Equal(12, version.ChunkCount);
        Assert.Equal(DocumentStatuses.ReadyForReview, (await check.KnowledgeDocuments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, rag.IngestCalls);
    }

    [PostgresFact]
    public async Task The_stored_lease_is_the_microsecond_value_its_owner_holds_and_a_shutdown_release_matches_it()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var leasedAt = ctx.Clock.GetUtcNow();
        KnowledgeIngestionJob? running = null;
        using var shutdown = new CancellationTokenSource();
        var rag = new FakeRagServiceClient
        {
            Ingest = async (_, token) =>
            {
                await using (var db = database.NewContext())
                    running = await db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
                shutdown.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return null!;
            },
        };

        await using (var db = database.NewContext())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, shutdown.Token));

        Assert.Equal(JobStatuses.Running, running!.Status);
        Assert.NotEqual(leasedAt + Lease, running.LockedUntil);           // the clock has sub-microsecond ticks...
        Assert.Equal(Micro(leasedAt + Lease), running.LockedUntil);       // ...and the database keeps whole microseconds
        // The owner's release (fenced on the same lease) found its lease: the attempt is given back.
        await using var check = database.NewContext();
        var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.LockedUntil);
        Assert.Equal(VersionStatuses.Registered, (await check.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task A_slow_worker_whose_lease_was_taken_over_cannot_overwrite_the_newer_workers_outcome()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var newerStarted = new TaskCompletionSource();
        var newerResult = new TaskCompletionSource<RagIngestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerRag = new FakeRagServiceClient { Ingest = (_, _) => { newerStarted.TrySetResult(); return newerResult.Task; } };
        Task<bool>? newer = null;
        var staleRag = new FakeRagServiceClient
        {
            Ingest = async (request, _) =>
            {
                // The first worker is slow: its lease expires and a second worker takes the job over.
                ctx.Clock.Now += Lease + TimeSpan.FromSeconds(1);
                newer = Task.Run(async () =>
                {
                    await using var db = database.NewContext();
                    return await Ingestion(db, newerRag, options, ctx.Clock).RunAsync(jobId, None);
                });
                await newerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // Then the stale worker's call succeeds.
                return new RagIngestResult(true, "READY", 3, 12, 4000, "qwen3-embedding:0.6b", 1024, null,
                    request.ConfigVersion, request.ChunkCharacters, request.ChunkOverlap);
            },
        };

        await using (var db = database.NewContext())
            Assert.True(await Ingestion(db, staleRag, options, ctx.Clock).RunAsync(jobId, None));

        await using (var check = database.NewContext())
        {
            var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatuses.Running, job.Status);                 // the stale READY was not recorded
            Assert.Equal(2, job.Attempts);
            Assert.NotNull(job.LockedUntil);
            Assert.Null((await check.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).ChunkCount);
        }

        // The newer worker (the lease owner) fails: its outcome stands.
        newerResult.SetResult(new RagIngestResult(false, "FAILED", null, null, null, null, null, "unavailable"));
        Assert.True(await newer!.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var after = database.NewContext();
        var final = await after.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.RetryWaiting, final.Status);
        Assert.Equal(2, final.Attempts);
        Assert.Null(final.LockedUntil);
        Assert.Equal(VersionStatuses.Registered, (await after.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task Concurrent_workers_run_the_job_once_and_the_owner_completes_it()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var rag = new FakeRagServiceClient();

        var ran = await RaceAsync(database, 4, (db, _) => Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, None));

        Assert.Single(ran, r => r);
        Assert.Equal(1, rag.IngestCalls);
        await using var check = database.NewContext();
        var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal((JobStatuses.Completed, 1), (job.Status, job.Attempts));
        Assert.Equal(VersionStatuses.ReadyForReview, (await check.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>Runs once, right after the outcome transaction has read the job (between the fencing check and the write).</summary>
    private sealed class AfterJobRead(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.Transaction is not null && command.CommandText.Contains("FROM knowledge_ingestion_job", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    [PostgresFact]
    public async Task A_takeover_racing_the_owners_outcome_write_cannot_lease_the_job_it_is_completing()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var later = ctx.Clock.GetUtcNow() + Lease + TimeSpan.FromMinutes(1);
        Task<bool>? takeover = null;
        var interceptor = new AfterJobRead(async () =>
        {
            // Another worker, whose clock says the lease has expired, tries to lease the job while the owner is recording its outcome.
            takeover = Task.Run(async () =>
            {
                await using var db = database.NewContext();
                return await new KnowledgeUnitOfWork(db).TryLeaseJobAsync(jobId, later, Micro(later + Lease), None);
            });
            await Task.WhenAny(takeover, Task.Delay(TimeSpan.FromSeconds(1)));
        });
        var rag = new FakeRagServiceClient();

        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                         .UseNpgsql(database.ConnectionString).AddInterceptors(interceptor).Options))
            Assert.True(await Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, None));

        Assert.NotNull(takeover);
        Assert.False(await takeover!.WaitAsync(TimeSpan.FromSeconds(30)));    // waited for the owner's commit, then found nothing due
        await using var check = database.NewContext();
        var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal((JobStatuses.Completed, 1), (job.Status, job.Attempts));
        Assert.Null(job.LockedUntil);
    }

    [PostgresFact]
    public async Task Failed_ingestions_back_off_and_end_failed_after_the_last_attempt()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var rag = new FakeRagServiceClient { Unavailable = true };

        foreach (var (attempt, backoff) in new[] { (1, 30), (2, 60) })
        {
            var at = ctx.Clock.GetUtcNow();
            await using (var db = database.NewContext())
                Assert.Equal(1, await Ingestion(db, rag, options, ctx.Clock).RunDueAsync(5, None));
            await using var check = database.NewContext();
            var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal((JobStatuses.RetryWaiting, attempt), (job.Status, job.Attempts));
            Assert.Equal(Micro(at.AddSeconds(backoff)), job.NextAttemptAt);
            Assert.Null(job.LockedUntil);
            await using (var db = database.NewContext())
                Assert.Equal(0, await Ingestion(db, rag, options, ctx.Clock).RunDueAsync(5, None));   // not due before the backoff
            ctx.Clock.Now += TimeSpan.FromSeconds(backoff + 1);
        }

        await using (var db = database.NewContext())
            Assert.Equal(1, await Ingestion(db, rag, options, ctx.Clock).RunDueAsync(5, None));
        await using var final = database.NewContext();
        var failed = await final.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal((JobStatuses.Failed, 3), (failed.Status, failed.Attempts));
        Assert.Null(failed.LockedUntil);
        Assert.Equal(VersionStatuses.Failed, (await final.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(DocumentStatuses.Failed, (await final.KnowledgeDocuments.AsNoTracking().SingleAsync()).Status);
        ctx.Clock.Now += TimeSpan.FromHours(2);
        await using (var db = database.NewContext())
            Assert.False(await Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, None));
        Assert.Equal(3, rag.IngestCalls);
    }

    [PostgresFact]
    public async Task A_completed_ingestion_is_not_run_again_and_creates_no_second_job()
    {
        var options = Options();
        var (database, ctx, jobId) = await RegisteredAsync(options);
        await using var _ = database;
        await using var __ = ctx;
        var rag = new FakeRagServiceClient();
        await using (var db = database.NewContext())
            Assert.Equal(1, await Ingestion(db, rag, options, ctx.Clock).RunDueAsync(5, None));

        // Later sweeps, including after the lease would have expired, find nothing to do.
        foreach (var step in new[] { TimeSpan.Zero, Lease + TimeSpan.FromMinutes(1) })
        {
            ctx.Clock.Now += step;
            await using var db = database.NewContext();
            Assert.Equal(0, await Ingestion(db, rag, options, ctx.Clock).RunDueAsync(5, None));
            Assert.False(await Ingestion(db, rag, options, ctx.Clock).RunAsync(jobId, None));
        }

        Assert.Equal(1, rag.IngestCalls);                                      // chunks were written by one ingest call only
        await using var check = database.NewContext();
        var job = await check.KnowledgeIngestionJobs.AsNoTracking().SingleAsync();
        Assert.Equal((jobId, JobStatuses.Completed, 1), (job.Id, job.Status, job.Attempts));
        Assert.Equal(VersionStatuses.ReadyForReview, (await check.KnowledgeDocumentVersions.AsNoTracking().SingleAsync()).Status);
    }
}
