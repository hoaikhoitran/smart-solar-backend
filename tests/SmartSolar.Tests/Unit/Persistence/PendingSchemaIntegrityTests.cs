using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Database-level guards added before the migration: a VERIFIED file always has a lowercase SHA-256, and a
/// document's latest/published version must exist and belong to that document (composite foreign keys).
/// </summary>
public sealed class PendingSchemaIntegrityTests
{
    private static StoredFile File(Guid uploader, string status, string? checksum) => new()
    {
        Id = Guid.NewGuid(), Purpose = FilePurposes.KnowledgeDocument, Provider = "S3", Bucket = "b", ObjectKey = $"knowledge/{uploader}/{Guid.NewGuid():N}.pdf",
        OriginalFileName = "f.pdf", DeclaredContentType = "application/pdf", DeclaredSizeBytes = 10, Status = status, UploadedByUserId = uploader,
        VerifiedContentType = status == FileStatuses.Verified ? "application/pdf" : null, VerifiedSizeBytes = status == FileStatuses.Verified ? 10 : null,
        VerifiedAt = status == FileStatuses.Verified ? DateTimeOffset.UtcNow : null, VerifiedChecksumSha256 = checksum,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_verified_file_without_a_lowercase_sha256_is_rejected_by_the_database()
    {
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync("Uploader");

        foreach (var checksum in new string?[] { null, new('A', 64), new('a', 63) })
        {
            ctx.Db.StoredFiles.Add(File(user.Id, FileStatuses.Verified, checksum));
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.Db.SaveChangesAsync());
            ctx.Db.ChangeTracker.Clear();
        }
        ctx.Db.StoredFiles.Add(File(user.Id, FileStatuses.Verified, new string('a', 64)));
        ctx.Db.StoredFiles.Add(File(user.Id, FileStatuses.PendingVerification, null));
        await ctx.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_document_cannot_point_at_a_missing_version_or_at_another_documents_version()
    {
        await using var ctx = new PreSurveyTestContext();
        var admin = await ctx.SeedUserAsync("Admin");
        var file = File(admin.Id, FileStatuses.Verified, new string('a', 64));
        var otherFile = File(admin.Id, FileStatuses.Verified, new string('b', 64));
        ctx.Db.StoredFiles.AddRange(file, otherFile);
        KnowledgeDocument Document() => new()
        {
            Id = Guid.NewGuid(), Title = "T", Category = "FAQ", Status = DocumentStatuses.Registered, CreatedByUserId = admin.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var first = Document();
        var second = Document();
        ctx.Db.KnowledgeDocuments.AddRange(first, second);
        await ctx.Db.SaveChangesAsync();
        var secondsVersion = new KnowledgeDocumentVersion
        {
            Id = Guid.NewGuid(), DocumentId = second.Id, VersionNumber = 1, FileId = otherFile.Id, Status = VersionStatuses.ReadyForReview,
            CreatedByUserId = admin.Id, CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.KnowledgeDocumentVersions.Add(secondsVersion);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        async Task RejectedAsync(Action<KnowledgeDocument> change)
        {
            var tracked = await ctx.Db.KnowledgeDocuments.SingleAsync(d => d.Id == first.Id);
            change(tracked);
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.Db.SaveChangesAsync());
            ctx.Db.ChangeTracker.Clear();
        }

        await RejectedAsync(d => d.LatestVersionId = Guid.NewGuid());                // no such version
        await RejectedAsync(d => d.LatestVersionId = secondsVersion.Id);             // belongs to another document
        await RejectedAsync(d => { d.PublishedVersionId = secondsVersion.Id; d.Status = DocumentStatuses.Published; });

        var own = await ctx.Db.KnowledgeDocuments.SingleAsync(d => d.Id == second.Id);
        own.LatestVersionId = secondsVersion.Id;                                      // its own version: accepted
        await ctx.Db.SaveChangesAsync();
    }
}
