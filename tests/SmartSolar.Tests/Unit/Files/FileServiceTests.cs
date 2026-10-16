using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Files;

public sealed class FileServiceTests
{
    private const string Bucket = "smartsolar-files";

    private static (FileService Service, FakeStorageObjectVerifier Storage, PreSurveyTestContext Ctx) Create()
    {
        var ctx = new PreSurveyTestContext();
        var storage = new FakeStorageObjectVerifier();
        var service = new FileService(new FileStore(ctx.Db), storage, new StorageOptions { DefaultBucket = Bucket },
            TimeProvider.System, NullLogger<FileService>.Instance);
        return (service, storage, ctx);
    }

    private static RegisterFileCommand Command(Guid user, string purpose, string key, string name, string type, long size, string? sha = null)
        => new(user, purpose, null, key, name, type, size, sha);

    [Fact]
    public async Task A_matching_object_in_storage_becomes_verified_with_a_backend_computed_checksum()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Sales");
        var key = $"contracts/official/{user.Id}/contract-v1.pdf";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.PdfBytes);

        var result = await service.RegisterAsync(Command(user.Id, FilePurposes.ContractDocument, key, "contract-v1.pdf", "application/pdf",
            FakeStorageObjectVerifier.PdfBytes.Length), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(FileStatuses.Verified, result.Value!.Status);
        Assert.Equal(FakeStorageObjectVerifier.Sha256(FakeStorageObjectVerifier.PdfBytes), result.Value.ChecksumSha256);
        Assert.Equal("application/pdf", result.Value.ContentType);
        Assert.Equal("v1", result.Value.ObjectVersionId);
    }

    [Theory]
    [InlineData("contracts/official/{other}/x.pdf", "another user's prefix")]
    [InlineData("contracts/official/{me}/../../secrets.pdf", "traversal")]
    [InlineData("https://evil.example/x.pdf", "url")]
    [InlineData("/contracts/official/{me}/x.pdf", "absolute")]
    [InlineData("onsite-surveys/{me}/x.pdf", "wrong purpose prefix")]
    [InlineData("contracts/official/{me}//x.pdf", "empty segment")]
    public async Task Keys_outside_the_uploaders_own_prefix_are_rejected(string template, string why)
    {
        var (service, _, ctx) = Create();
        await using var __ = ctx;
        var user = await ctx.SeedUserAsync("Sales");
        var key = template.Replace("{me}", user.Id.ToString()).Replace("{other}", Guid.NewGuid().ToString());

        var result = await service.RegisterAsync(Command(user.Id, FilePurposes.ContractDocument, key, "x.pdf", "application/pdf", 100), CancellationToken.None);

        Assert.True(result.ErrorCode == FileErrorCodes.InvalidObjectKey, why);
    }

    [Fact]
    public async Task Disguised_content_size_mismatch_and_wrong_checksum_are_rejected()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Tech");
        var fake = $"installations/{user.Id}/photo.jpg";
        storage.Put(Bucket, fake, FakeStorageObjectVerifier.PdfBytes);          // a PDF renamed .jpg
        var shortKey = $"installations/{user.Id}/short.png";
        storage.Put(Bucket, shortKey, FakeStorageObjectVerifier.PngBytes);
        var signed = $"contracts/signed/{user.Id}/signed.pdf";
        storage.Put(Bucket, signed, FakeStorageObjectVerifier.PdfBytes);

        var disguised = await service.RegisterAsync(Command(user.Id, FilePurposes.InstallationEvidence, fake, "photo.jpg", "image/jpeg", FakeStorageObjectVerifier.PdfBytes.Length), CancellationToken.None);
        var sizeLie = await service.RegisterAsync(Command(user.Id, FilePurposes.InstallationEvidence, shortKey, "short.png", "image/png", 999), CancellationToken.None);
        var checksumLie = await service.RegisterAsync(Command(user.Id, FilePurposes.SignedContract, signed, "signed.pdf", "application/pdf",
            FakeStorageObjectVerifier.PdfBytes.Length, new string('a', 64)), CancellationToken.None);

        Assert.Equal(FileStatuses.Rejected, disguised.Value!.Status);
        Assert.Equal(FileStatuses.Rejected, sizeLie.Value!.Status);
        Assert.Equal(FileStatuses.Rejected, checksumLie.Value!.Status);
    }

    [Fact]
    public async Task Missing_storage_access_keeps_files_pending_and_unusable_until_reverified()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Tech");
        var key = $"onsite-surveys/{user.Id}/roof.jpg";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.JpegBytes);
        storage.Unavailable = true;

        var pending = await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, key, "roof.jpg", "image/jpeg", FakeStorageObjectVerifier.JpegBytes.Length), CancellationToken.None);
        Assert.Equal(FileStatuses.PendingVerification, pending.Value!.Status);
        Assert.Equal(FileErrorCodes.NotVerified,
            (await service.RequireVerifiedAsync([pending.Value.FileId], FilePurposes.OnSiteSurveyEvidence, user.Id, CancellationToken.None)).ErrorCode);

        storage.Unavailable = false;
        var verified = await service.VerifyAsync(pending.Value.FileId, user.Id, false, CancellationToken.None);
        Assert.Equal(FileStatuses.Verified, verified.Value!.Status);
        Assert.True((await service.RequireVerifiedAsync([pending.Value.FileId], FilePurposes.OnSiteSurveyEvidence, user.Id, CancellationToken.None)).Succeeded);
        Assert.Equal(FileErrorCodes.NotOwned,
            (await service.RequireVerifiedAsync([pending.Value.FileId], FilePurposes.OnSiteSurveyEvidence, Guid.NewGuid(), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.WrongPurpose,
            (await service.RequireVerifiedAsync([pending.Value.FileId], FilePurposes.InstallationEvidence, user.Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task The_same_object_cannot_be_registered_twice_and_other_users_cannot_see_it()
    {
        var (service, storage, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Tech");
        var key = $"onsite-surveys/{user.Id}/roof.png";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.PngBytes);
        var first = await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, key, "roof.png", "image/png", FakeStorageObjectVerifier.PngBytes.Length), CancellationToken.None);

        var second = await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, key, "roof.png", "image/png", FakeStorageObjectVerifier.PngBytes.Length), CancellationToken.None);

        Assert.Equal(FileErrorCodes.AlreadyRegistered, second.ErrorCode);
        Assert.Equal(FileErrorCodes.NotFound, (await service.GetAsync(first.Value!.FileId, Guid.NewGuid(), false, CancellationToken.None)).ErrorCode);
        Assert.True((await service.GetAsync(first.Value.FileId, Guid.NewGuid(), true, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Unknown_purposes_types_buckets_and_oversized_files_are_refused_at_registration()
    {
        var (service, _, ctx) = Create();
        await using var _ = ctx;
        var user = await ctx.SeedUserAsync("Tech");
        var key = $"onsite-surveys/{user.Id}/roof.exe";

        Assert.Equal(FileErrorCodes.InvalidPurpose, (await service.RegisterAsync(Command(user.Id, "AVATAR", key, "a.png", "image/png", 10), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.TypeNotAllowed, (await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, key, "roof.exe", "application/x-msdownload", 10), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.TypeNotAllowed, (await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, $"onsite-surveys/{user.Id}/a.pdf", "a.png", "image/png", 10), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.TooLarge, (await service.RegisterAsync(Command(user.Id, FilePurposes.OnSiteSurveyEvidence, $"onsite-surveys/{user.Id}/a.png", "a.png", "image/png", 999_999_999), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.BucketNotAllowed, (await service.RegisterAsync(
            new RegisterFileCommand(user.Id, FilePurposes.OnSiteSurveyEvidence, "someone-elses-bucket", $"onsite-surveys/{user.Id}/a.png", "a.png", "image/png", 10, null), CancellationToken.None)).ErrorCode);
    }
}
