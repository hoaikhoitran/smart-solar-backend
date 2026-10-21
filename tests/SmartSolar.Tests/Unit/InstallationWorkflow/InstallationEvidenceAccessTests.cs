using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.Unit.InstallationWorkflow.InstallationTests;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Installation evidence read access over a real project (Flow 1 → contract → payment → assignment → started → one verified
/// photo): who may list evidence and obtain a presigned read URL, and every refusal. The signer is synthetic.
/// </summary>
public sealed class InstallationEvidenceAccessTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record Setup(InstallationWorkflowTestContext Ctx, Paid Paid, Guid ProjectId, Guid Technician, Guid OtherTechnician, Guid EvidenceId,
        FakeStorageUrlSigner Signer, CapturingLoggerProvider Logs);

    private static InstallationEvidenceHandlers Handlers(Setup s, StorageOptions? options = null)
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(s.Logs));
        var urls = new FileReadUrlService(s.Signer, s.Ctx.Storage, options ?? s.Ctx.StorageOptions, s.Ctx.Clock, factory.CreateLogger<FileReadUrlService>());
        return new InstallationEvidenceHandlers(new InstallationUnitOfWork(s.Ctx.Db), new FileStore(s.Ctx.Db), urls, s.Ctx.Scheduling.Calendar,
            factory.CreateLogger<InstallationEvidenceHandlers>());
    }

    private static async Task<Setup> WithEvidenceAsync(InstallationWorkflowTestContext ctx)
    {
        var (paid, project, technician, other) = await ScheduledAsync(ctx);
        await StartAsync(ctx, project.ProjectId, technician);
        var view = await AddCompletionEvidenceAsync(ctx, project.ProjectId, technician);
        ctx.Db.ChangeTracker.Clear();
        return new Setup(ctx, paid, project.ProjectId, technician, other, Assert.Single(view.Evidence).EvidenceId, new FakeStorageUrlSigner(ctx.Clock),
            new CapturingLoggerProvider());
    }

    private static async Task<StoredFile> EvidenceFileAsync(Setup s)
    {
        var fileId = (await s.Ctx.Db.InstallationEvidence.AsNoTracking().SingleAsync(e => e.Id == s.EvidenceId)).FileId;
        return await s.Ctx.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == fileId);
    }

    [Fact]
    public async Task The_customer_lists_own_evidence_and_gets_inline_and_attachment_urls_pinned_to_the_verified_version()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);
        var file = await EvidenceFileAsync(s);

        var list = await handlers.GetEvidenceAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, None);
        Assert.True(list.Succeeded, list.Message);
        var item = Assert.Single(list.Value!);
        Assert.Equal(s.EvidenceId, item.EvidenceId);
        Assert.Equal(FilePolicies.Jpeg, item.ContentType);
        Assert.Equal(FakeStorageObjectVerifier.JpegBytes.Length, item.SizeBytes);
        Assert.Equal(file.VerifiedChecksumSha256, item.Sha256);
        Assert.Equal("Array and inverter", item.Caption);
        Assert.True(item.Downloadable);

        var inline = await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None);
        Assert.True(inline.Succeeded, inline.Message);
        Assert.Equal("inline", inline.Value!.Disposition);
        Assert.Equal(FilePolicies.Jpeg, inline.Value.ContentType);
        Assert.Equal(ctx.Clock.GetUtcNow().AddSeconds(300), inline.Value.ExpiresAt);
        var attachment = await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Attachment, None);
        Assert.Equal("attachment", attachment.Value!.Disposition);

        Assert.All(s.Signer.Calls, c => Assert.Equal(new StorageObjectLocation(file.Bucket, file.ObjectKey, file.ObjectVersionId), c.Location));
        Assert.NotNull(file.ObjectVersionId);

        // Logs carry ids and outcomes only: never the URL, the object key or the signature.
        Assert.NotEmpty(s.Logs.Messages);
        Assert.All(s.Logs.Messages, m =>
        {
            Assert.DoesNotContain(file.ObjectKey, m);
            Assert.DoesNotContain(FakeStorageUrlSigner.Host, m);
            Assert.DoesNotContain(FakeStorageUrlSigner.Signature, m);
        });
    }

    /// <summary>A second, unrelated project row (foreign keys off for the insert: only the project row matters here).</summary>
    internal static async Task<Guid> SeedBareProjectAsync(AppDbContext db, Guid customerUserId, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try
        {
            db.InstallationProjects.Add(new InstallationProject
            {
                Id = id, ContractId = Guid.NewGuid(), SurveyRequestId = Guid.NewGuid(), CustomerUserId = customerUserId, PropertySiteId = Guid.NewGuid(),
                ManagerUserId = Guid.NewGuid(), Status = InstallationStatuses.PendingAssignment, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
            db.ChangeTracker.Clear();
        }
        return id;
    }

    [Fact]
    public async Task Other_customers_and_uninvolved_technicians_get_404_and_nothing_is_signed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);

        foreach (var (role, user) in new[] { (EvidenceViewerRole.Customer, Guid.NewGuid()), (EvidenceViewerRole.Customer, s.Technician),
                     (EvidenceViewerRole.Technician, s.OtherTechnician), (EvidenceViewerRole.Technician, s.Paid.CustomerId) })
        {
            Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await handlers.GetEvidenceAsync(role, user, s.ProjectId, None)).ErrorCode);
            Assert.Equal(InstallationErrorCodes.ProjectNotFound,
                (await handlers.CreateEvidenceUrlAsync(role, user, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        }
        Assert.Empty(s.Signer.Calls);
    }

    [Fact]
    public async Task The_responsible_technician_and_staff_may_read_the_evidence()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);

        Assert.Single((await handlers.GetEvidenceAsync(EvidenceViewerRole.Technician, s.Technician, s.ProjectId, None)).Value!);
        Assert.True((await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Technician, s.Technician, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).Succeeded);
        Assert.Single((await handlers.GetEvidenceAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, s.ProjectId, None)).Value!);
        Assert.True((await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, Guid.NewGuid(), s.ProjectId, s.EvidenceId, StorageUrlDisposition.Attachment, None)).Succeeded);

        // Once the technician is no longer on the project (assignment closed without completion) access ends.
        var assignment = await ctx.Db.InstallationAssignments.SingleAsync(a => a.ProjectId == s.ProjectId && a.TechnicianUserId == s.Technician);
        assignment.Status = SmartSolar.Modules.Installations.AssignmentStatuses.Replaced;
        assignment.ClosedAt = ctx.Clock.GetUtcNow();
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(InstallationErrorCodes.ProjectNotFound,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Technician, s.Technician, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
    }

    [Fact]
    public async Task Unknown_projects_unknown_evidence_and_evidence_of_another_project_are_404()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);
        var otherCustomer = Guid.NewGuid();
        var otherProjectId = await SeedBareProjectAsync(ctx.Db, otherCustomer, ctx.Clock.GetUtcNow());

        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await handlers.GetEvidenceAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, Guid.NewGuid(), None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, Guid.NewGuid(), s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.EvidenceNotFound,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, s.ProjectId, Guid.NewGuid(), StorageUrlDisposition.Inline, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.EvidenceNotFound,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, otherProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        // The other project's customer cannot reach this evidence through their own project either.
        Assert.Equal(InstallationErrorCodes.EvidenceNotFound,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, otherCustomer, otherProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        Assert.Empty((await handlers.GetEvidenceAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, otherProjectId, None)).Value!);
        Assert.Empty(s.Signer.Calls);
        Assert.Equal(404, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(InstallationErrorCodes.EvidenceNotFound));
    }

    [Fact]
    public async Task A_deleted_object_is_410_an_outage_or_missing_signer_is_503()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);
        var file = await EvidenceFileAsync(s);

        ctx.Storage.Unavailable = true;
        Assert.Equal(StorageErrorCodes.Unavailable,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        ctx.Storage.Unavailable = false;

        s.Signer.IsConfigured = false;
        Assert.Equal(StorageErrorCodes.NotConfigured,
            (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode);
        s.Signer.IsConfigured = true;

        ctx.Storage.DeleteVersion(file.Bucket, file.ObjectKey, file.ObjectVersionId!);
        var gone = await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None);
        Assert.Equal(InstallationErrorCodes.EvidenceObjectMissing, gone.ErrorCode);
        Assert.Equal(410, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(gone.ErrorCode!));
        Assert.Empty(s.Signer.Calls);
        Assert.All(s.Logs.Messages, m => Assert.DoesNotContain(file.ObjectKey, m));
    }

    [Fact]
    public async Task Files_that_are_no_longer_verified_or_point_outside_their_prefix_are_not_signable()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var handlers = Handlers(s);
        var fileId = (await EvidenceFileAsync(s)).Id;

        async Task<string?> AfterAsync(Action<StoredFile> change)
        {
            var row = await ctx.Db.StoredFiles.SingleAsync(f => f.Id == fileId);
            var original = (row.Status, row.Purpose, row.ObjectKey);
            change(row);
            await ctx.Db.SaveChangesAsync();
            ctx.Db.ChangeTracker.Clear();
            var code = (await handlers.CreateEvidenceUrlAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None)).ErrorCode;
            row = await ctx.Db.StoredFiles.SingleAsync(f => f.Id == fileId);
            (row.Status, row.Purpose, row.ObjectKey) = original;
            await ctx.Db.SaveChangesAsync();
            ctx.Db.ChangeTracker.Clear();
            return code;
        }

        Assert.Equal(FileErrorCodes.NotVerified, await AfterAsync(f => f.Status = FileStatuses.Rejected));
        Assert.Equal(FileErrorCodes.NotSignable, await AfterAsync(f => f.Purpose = FilePurposes.ProjectDocument));
        Assert.Equal(FileErrorCodes.NotSignable, await AfterAsync(f => f.ObjectKey = $"contracts/signed/{Guid.NewGuid()}/x.jpg"));
        Assert.Equal(FileErrorCodes.NotSignable, await AfterAsync(f => f.ObjectKey = $"installations/{Guid.NewGuid()}/../../contracts/x.jpg"));
        Assert.Empty(s.Signer.Calls);

        var listed = Assert.Single((await handlers.GetEvidenceAsync(EvidenceViewerRole.Staff, s.Paid.ManagerId, s.ProjectId, None)).Value!);
        Assert.True(listed.Downloadable);
    }

    [Fact]
    public async Task The_lifetime_comes_from_configuration()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await WithEvidenceAsync(ctx);
        var options = new StorageOptions { DefaultBucket = InstallationWorkflowTestContext.Bucket, S3 = new S3VerificationOptions { PresignedUrlTtlSeconds = 60 } };
        var result = await Handlers(s, options).CreateEvidenceUrlAsync(EvidenceViewerRole.Customer, s.Paid.CustomerId, s.ProjectId, s.EvidenceId, StorageUrlDisposition.Inline, None);

        Assert.Equal(ctx.Clock.GetUtcNow().AddSeconds(60), result.Value!.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(s.Signer.Calls).Request.TimeToLive);
    }
}
