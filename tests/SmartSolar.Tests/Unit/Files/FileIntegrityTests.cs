using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Files;

/// <summary>
/// A verified file must not change silently afterwards. References are pinned to the S3 object version
/// verified (versioning is required by default), every purpose gets a backend-computed SHA-256, and
/// <see cref="FileService.VerifyIntegrityAsync"/> re-checks the pinned version before critical decisions.
/// </summary>
public sealed class FileIntegrityTests
{
    private const string Bucket = "smartsolar-files";
    private static readonly CancellationToken None = CancellationToken.None;

    private static (FileService Service, FakeStorageObjectVerifier Storage, PreSurveyTestContext Ctx) Create(bool requireVersioning = true, bool versionedBucket = true)
    {
        var ctx = new PreSurveyTestContext();
        var storage = new FakeStorageObjectVerifier { Versioned = versionedBucket };
        var options = new StorageOptions { DefaultBucket = Bucket };
        options.S3.RequireObjectVersioning = requireVersioning;
        return (new FileService(new FileStore(ctx.Db), storage, options, TimeProvider.System, NullLogger<FileService>.Instance), storage, ctx);
    }

    private static async Task<(Guid FileId, string Key, Guid User)> VerifiedPdfAsync(FileService service, FakeStorageObjectVerifier storage, PreSurveyTestContext ctx,
        string purpose = FilePurposes.SignedContract)
    {
        var user = await ctx.SeedUserAsync($"Uploader {Guid.NewGuid():N}");
        var key = $"{FilePolicies.ByPurpose[purpose].KeyPrefix}{user.Id}/{Guid.NewGuid():N}.pdf";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.PdfBytes);
        var result = await service.RegisterAsync(new RegisterFileCommand(user.Id, purpose, null, key, "scan.pdf", FilePolicies.Pdf,
            FakeStorageObjectVerifier.PdfBytes.Length, null), None);
        Assert.True(result.Succeeded && result.Value!.Status == FileStatuses.Verified, result.Message ?? result.Value?.RejectionReason);
        return (result.Value!.FileId, key, user.Id);
    }

    private static readonly byte[] Forged = "%PDF-1.7\n% a different, forged document!!\n"u8.ToArray();

    [Fact]
    public async Task Overwriting_the_key_in_a_versioned_bucket_cannot_change_the_verified_file()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var (fileId, key, user) = await VerifiedPdfAsync(service, storage, ctx);
        var pinned = (await service.GetAsync(fileId, user, false, None)).Value!.ObjectVersionId;

        storage.Put(Bucket, key, Forged);   // a new version appears under the same key

        var check = await service.VerifyIntegrityAsync(fileId, None);
        Assert.True(check.Succeeded, check.Message);
        var verified = (await service.RequireVerifiedAsync([fileId], FilePurposes.SignedContract, user, None)).Value!.Single();
        Assert.Equal(pinned, verified.VersionId);          // consumers read the pinned version
        Assert.Equal(FakeStorageObjectVerifier.Sha256(FakeStorageObjectVerifier.PdfBytes), verified.ChecksumSha256);
    }

    [Fact]
    public async Task Objects_without_a_version_are_refused_when_versioning_is_required()
    {
        var (service, storage, ctx) = Create(requireVersioning: true, versionedBucket: false);
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Uploader");
        var key = $"contracts/signed/{user.Id}/scan.pdf";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.PdfBytes);

        var result = await service.RegisterAsync(new RegisterFileCommand(user.Id, FilePurposes.SignedContract, null, key, "scan.pdf", FilePolicies.Pdf,
            FakeStorageObjectVerifier.PdfBytes.Length, null), None);

        Assert.Equal(FileStatuses.Rejected, result.Value!.Status);
        Assert.Contains("versioning", result.Value.RejectionReason);
    }

    [Fact]
    public async Task Silent_replacement_in_an_unversioned_bucket_is_detected_and_the_file_is_rejected()
    {
        var (service, storage, ctx) = Create(requireVersioning: false, versionedBucket: false);
        await using var _ = ctx;
        var (fileId, key, user) = await VerifiedPdfAsync(service, storage, ctx);

        storage.Put(Bucket, key, Forged[..FakeStorageObjectVerifier.PdfBytes.Length]);   // same size, different bytes

        var check = await service.VerifyIntegrityAsync(fileId, None);
        Assert.Equal(FileErrorCodes.ContentChanged, check.ErrorCode);
        Assert.Equal(FileStatuses.Rejected, (await service.GetAsync(fileId, user, false, None)).Value!.Status);
        Assert.Equal(FileErrorCodes.NotVerified, (await service.RequireVerifiedAsync([fileId], FilePurposes.SignedContract, user, None)).ErrorCode);
    }

    [Fact]
    public async Task A_deleted_object_or_deleted_pinned_version_fails_the_integrity_check()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var (first, firstKey, user) = await VerifiedPdfAsync(service, storage, ctx);
        var (second, secondKey, _) = await VerifiedPdfAsync(service, storage, ctx);
        var pinned = (await service.GetAsync(second, user, true, None)).Value!.ObjectVersionId!;

        storage.Delete(Bucket, firstKey);
        storage.Put(Bucket, secondKey, Forged);
        storage.DeleteVersion(Bucket, secondKey, pinned);   // the verified version is gone; only a forged one remains

        Assert.Equal(FileErrorCodes.ContentChanged, (await service.VerifyIntegrityAsync(first, None)).ErrorCode);
        Assert.Equal(FileErrorCodes.ContentChanged, (await service.VerifyIntegrityAsync(second, None)).ErrorCode);
    }

    [Fact]
    public async Task An_unreachable_store_fails_closed_without_rejecting_the_file()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var (fileId, _, user) = await VerifiedPdfAsync(service, storage, ctx);
        storage.Unavailable = true;

        Assert.Equal(FileErrorCodes.StorageUnavailable, (await service.VerifyIntegrityAsync(fileId, None)).ErrorCode);
        Assert.Equal(FileStatuses.Verified, (await service.GetAsync(fileId, user, false, None)).Value!.Status);
    }

    [Fact]
    public async Task Integrity_checks_refuse_unknown_and_unverified_files()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        Assert.Equal(FileErrorCodes.NotFound, (await service.VerifyIntegrityAsync(Guid.NewGuid(), None)).ErrorCode);
        storage.Unavailable = true;
        var user = await ctx.SeedUserAsync("Uploader");
        var key = $"contracts/signed/{user.Id}/pending.pdf";
        var pending = (await service.RegisterAsync(new RegisterFileCommand(user.Id, FilePurposes.SignedContract, null, key, "p.pdf", FilePolicies.Pdf, 10, null), None)).Value!;
        Assert.Equal(FileErrorCodes.NotVerified, (await service.VerifyIntegrityAsync(pending.FileId, None)).ErrorCode);
    }

    [Fact]
    public async Task Every_purpose_gets_a_backend_computed_checksum_including_photo_evidence()
    {
        Assert.All(FilePolicies.ByPurpose.Values, p => Assert.True(p.RequireVerifiedChecksum, p.Purpose));
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Leader");
        var key = $"installations/{user.Id}/photo.jpg";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.JpegBytes);

        var result = await service.RegisterAsync(new RegisterFileCommand(user.Id, FilePurposes.InstallationEvidence, null, key, "photo.jpg", FilePolicies.Jpeg,
            FakeStorageObjectVerifier.JpegBytes.Length, null), None);

        Assert.Equal(FakeStorageObjectVerifier.Sha256(FakeStorageObjectVerifier.JpegBytes), result.Value!.ChecksumSha256);
    }
}
