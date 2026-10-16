using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.ExternalProviders.Rag;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// Durable ingestion under failures: provider timeouts, unexpected errors, service shutdown, worker crashes
/// (expired leases) and the retry budget. Attempts are counted when a job is leased, so a crashed run
/// still uses up an attempt and a job can never loop forever.
/// </summary>
public sealed class KnowledgeIngestionReliabilityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig(InstallationWorkflowTestContext ctx)
    {
        public InstallationWorkflowTestContext Ctx { get; } = ctx;
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32), MaxIngestAttempts = 3 };

        public KnowledgeIngestionService Ingestion(IRagServiceClient? rag = null)
            => new(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), rag ?? Rag, Options, Ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance);

        public async Task<(Guid DocumentId, Guid JobId)> RegisteredAsync()
        {
            var admin = (await Ctx.Scheduling.SeedTechnicianAsync($"Admin {Guid.NewGuid():N}", null, withRole: false)).Id;
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var handlers = new KnowledgeDocumentHandlers(new KnowledgeUnitOfWork(Ctx.Db), Ctx.Files(), Options, Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
            var doc = (await handlers.RegisterAsync(admin, file, "Guide", "FAQ", null, None)).Value!;
            return (doc.DocumentId, (await Ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync()).Id);
        }

        public Task<KnowledgeIngestionJob> JobAsync(Guid id) => Ctx.Db.KnowledgeIngestionJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        public Task<KnowledgeDocumentVersion> VersionAsync() => Ctx.Db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync();
        public Task<KnowledgeDocument> DocumentAsync() => Ctx.Db.KnowledgeDocuments.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task An_unexpected_provider_exception_counts_an_attempt_and_schedules_a_retry_instead_of_leaving_the_job_running()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Rag.Ingest = (_, _) => throw new InvalidOperationException("boom");
        var (_, jobId) = await rig.RegisteredAsync();

        Assert.True(await rig.Ingestion().RunAsync(jobId, None));

        var job = await rig.JobAsync(jobId);
        Assert.Equal(JobStatuses.RetryWaiting, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Null(job.LockedUntil);
        Assert.Equal(ctx.Clock.GetUtcNow().AddSeconds(30), job.NextAttemptAt);
        Assert.Equal(VersionStatuses.Registered, (await rig.VersionAsync()).Status);
    }

    [Fact]
    public async Task A_provider_timeout_is_a_failed_attempt_and_a_later_retry_succeeds()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        // The client turns its own timeout into an unreached result; simulate exactly that.
        rig.Rag.Ingest = (_, _) => Task.FromResult(new RagIngestResult(false, "FAILED", null, null, null, null, null, "timeout"));
        var (documentId, jobId) = await rig.RegisteredAsync();

        Assert.True(await rig.Ingestion().RunAsync(jobId, None));
        Assert.Equal(JobStatuses.RetryWaiting, (await rig.JobAsync(jobId)).Status);

        rig.Rag.Ingest = null;
        ctx.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        var job = await rig.JobAsync(jobId);
        Assert.Equal(JobStatuses.Completed, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(DocumentStatuses.ReadyForReview, (await rig.DocumentAsync()).Status);
        Assert.Equal(documentId, (await rig.VersionAsync()).DocumentId);
    }

    [Fact]
    public async Task Service_shutdown_releases_the_job_without_using_an_attempt()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Rag.Ingest = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null!; };
        var (_, jobId) = await rig.RegisteredAsync();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Ingestion().RunAsync(jobId, shutdown.Token));

        var job = await rig.JobAsync(jobId);
        Assert.Equal(JobStatuses.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.LockedUntil);
        Assert.Equal(VersionStatuses.Registered, (await rig.VersionAsync()).Status);
        // Picked up again straight away after the restart.
        rig.Rag.Ingest = null;
        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        Assert.Equal(JobStatuses.Completed, (await rig.JobAsync(jobId)).Status);
    }

    [Fact]
    public async Task Crashed_workers_use_up_attempts_and_the_job_ends_failed_instead_of_looping()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (_, jobId) = await rig.RegisteredAsync();
        var uow = new KnowledgeUnitOfWork(ctx.Db);

        // Three workers lease the job and die without finishing (process crash, container kill…).
        for (var crash = 1; crash <= rig.Options.MaxIngestAttempts; crash++)
        {
            var now = ctx.Clock.GetUtcNow();
            Assert.True(await uow.TryLeaseJobAsync(jobId, now, now.AddMinutes(7), None), $"crash {crash}");
            Assert.Equal(crash, (await rig.JobAsync(jobId)).Attempts);
            ctx.Clock.Now += TimeSpan.FromMinutes(8);   // lease expired
        }

        // The next sweep finalises the exhausted job instead of running it a fourth time.
        Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
        var job = await rig.JobAsync(jobId);
        Assert.Equal(JobStatuses.Failed, job.Status);
        Assert.Equal(rig.Options.MaxIngestAttempts, job.Attempts);
        Assert.Equal(0, rig.Rag.IngestCalls);
        Assert.Equal(VersionStatuses.Failed, (await rig.VersionAsync()).Status);
        Assert.Equal(DocumentStatuses.Failed, (await rig.DocumentAsync()).Status);
        Assert.Equal(0, await rig.Ingestion().RunDueAsync(10, None));
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_cannot_overwrite_the_result_of_the_worker_that_took_over()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var (_, jobId) = await rig.RegisteredAsync();
        var slow = new FakeRagServiceClient();
        var release = new TaskCompletionSource();
        slow.Ingest = async (_, _) =>
        {
            await release.Task;
            return new RagIngestResult(true, "OCR_REQUIRED", 1, 0, 0, null, null, "stale result");
        };

        var stale = rig.Ingestion(slow).RunAsync(jobId, None);          // leases, then hangs
        ctx.Clock.Now += TimeSpan.FromMinutes(8);                        // its lease expires
        Assert.True(await rig.Ingestion().RunAsync(jobId, None));        // another worker completes the job
        release.SetResult();
        await stale;

        var job = await rig.JobAsync(jobId);
        Assert.Equal(JobStatuses.Completed, job.Status);
        Assert.Equal(VersionStatuses.ReadyForReview, (await rig.VersionAsync()).Status);
    }

    [Fact]
    public async Task Repeated_failures_back_off_exponentially_and_end_failed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        rig.Rag.Unavailable = true;
        var (_, jobId) = await rig.RegisteredAsync();
        var delays = new List<TimeSpan>();

        for (var i = 0; i < rig.Options.MaxIngestAttempts; i++)
        {
            Assert.Equal(1, await rig.Ingestion().RunDueAsync(10, None));
            var job = await rig.JobAsync(jobId);
            if (job.Status == JobStatuses.RetryWaiting) delays.Add(job.NextAttemptAt - ctx.Clock.GetUtcNow());
            ctx.Clock.Now += TimeSpan.FromHours(2);
        }

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], delays);
        Assert.Equal(JobStatuses.Failed, (await rig.JobAsync(jobId)).Status);
        Assert.Equal(DocumentStatuses.Failed, (await rig.DocumentAsync()).Status);
    }

    // ---------------------------------------------------------------- HTTP client

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static RagServiceClient Client(RagOptions options)
        => new(new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://rag.test/") }, options, TimeProvider.System, NullLogger<RagServiceClient>.Instance);

    private static RagIngestRequest IngestRequest() => new(Guid.NewGuid(), Guid.NewGuid(), "b", "k", null, "application/pdf", new string('a', 64), "t", 1);

    [Fact]
    public async Task The_client_reports_its_own_timeout_as_an_unreached_result()
    {
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32), IngestTimeoutSeconds = 1, RequestTimeoutSeconds = 1 };

        var ingest = await Client(options).IngestAsync(IngestRequest(), None);
        var answer = await Client(options).AnswerAsync(new RagAnswerRequest("q", [], [], 1, 100, 100), None);
        var deleted = await Client(options).DeleteVectorsAsync(Guid.NewGuid(), None);

        Assert.False(ingest.Reached);
        Assert.Equal("timeout", ingest.Error);
        Assert.False(answer.Reached);
        Assert.False(deleted);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_a_timeout()
    {
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32), IngestTimeoutSeconds = 60 };
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(options).IngestAsync(IngestRequest(), shutdown.Token));
    }
}
