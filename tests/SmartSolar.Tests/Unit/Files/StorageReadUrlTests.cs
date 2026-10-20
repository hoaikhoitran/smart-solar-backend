using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Storage;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Files;

/// <summary>
/// Presigned read URLs: the real S3 signer (SigV4 query signing is a local computation; FAKE static credentials and a
/// reserved, never-resolved endpoint, so no network is involved), Content-Disposition sanitization, and the
/// <see cref="FileReadUrlService"/> scope checks in front of any signer.
/// </summary>
public sealed class StorageReadUrlTests
{
    private const string Bucket = "smartsolar-test";
    private const string FakeAccessKeyId = "TESTACCESSKEY1234567";
    private const string FakeSecret = "fake/SECRET+never-real/0000000000000000000";
    private static readonly CancellationToken None = CancellationToken.None;

    private static StorageOptions Options(string serviceUrl = "https://s3.storage.invalid", int ttl = 300, bool verifyFirst = true) => new()
    {
        DefaultBucket = Bucket,
        S3 = new S3VerificationOptions
        {
            Enabled = true, Region = "ap-southeast-1", ServiceUrl = serviceUrl, ForcePathStyle = true,
            AccessKeyId = FakeAccessKeyId, SecretAccessKey = FakeSecret, PresignedUrlTtlSeconds = ttl, VerifyObjectBeforeSigning = verifyFirst,
        },
    };

    private static S3StorageUrlSigner RealSigner(StorageOptions options, TimeProvider? time = null)
        => new(options, time ?? TimeProvider.System, NullLogger<S3StorageUrlSigner>.Instance);

    private static string EvidenceKey() => $"{FilePolicies.ByPurpose[FilePurposes.InstallationEvidence].KeyPrefix}{Guid.NewGuid()}/{Guid.NewGuid():N}.jpg";

    // ------------------------------------------------------------------ real S3 signer (no network)

    [Fact]
    public async Task S3_signer_pins_the_version_overrides_response_headers_and_uses_the_configured_ttl()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var signer = RealSigner(Options(ttl: 120), clock);
        var key = EvidenceKey();

        // A per-request lifetime above the configured one is capped to it.
        var result = await signer.CreateReadUrlAsync(new StorageObjectLocation(Bucket, key, "3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY"),
            new StorageUrlRequest(StorageUrlDisposition.Attachment, "roof \"array\".jpg", FilePolicies.Jpeg, TimeSpan.FromHours(1)), None);

        Assert.Equal(StorageUrlStatus.Signed, result.Status);
        var uri = new Uri(result.Url!);
        Assert.Equal("https", uri.Scheme);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("120", query["X-Amz-Expires"]);
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), result.ExpiresAt);
        Assert.Equal("3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY", query["versionId"]);
        Assert.Equal(FilePolicies.Jpeg, query["response-content-type"]);
        var disposition = query["response-content-disposition"]!;
        Assert.StartsWith("attachment; filename=\"roof array.jpg\"", disposition);
        Assert.Contains("filename*=UTF-8''roof%20array.jpg", disposition);
        Assert.False(string.IsNullOrEmpty(query["X-Amz-Signature"]));
        Assert.Contains(FakeAccessKeyId, query["X-Amz-Credential"]);
        Assert.DoesNotContain(FakeSecret, result.Url);
        Assert.DoesNotContain(Uri.EscapeDataString(FakeSecret), result.Url);
        Assert.DoesNotContain("SECRET", result.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task S3_signer_uses_https_unless_the_endpoint_is_explicitly_http_and_signs_inline_without_version()
    {
        using var https = RealSigner(Options(ttl: 300));
        var secure = await https.CreateReadUrlAsync(new StorageObjectLocation(Bucket, EvidenceKey()),
            new StorageUrlRequest(StorageUrlDisposition.Inline, "photo.jpg", FilePolicies.Jpeg, TimeSpan.FromSeconds(60)), None);
        Assert.Equal("https", new Uri(secure.Url!).Scheme);
        var query = HttpUtility.ParseQueryString(new Uri(secure.Url!).Query);
        Assert.Equal("60", query["X-Amz-Expires"]);
        Assert.Null(query["versionId"]);
        Assert.StartsWith("inline; filename=\"photo.jpg\"", query["response-content-disposition"]);

        using var local = RealSigner(Options(serviceUrl: "http://localhost:9000"));
        var emulator = await local.CreateReadUrlAsync(new StorageObjectLocation(Bucket, EvidenceKey(), "v1"),
            new StorageUrlRequest(StorageUrlDisposition.Inline, "photo.jpg", FilePolicies.Jpeg, TimeSpan.FromSeconds(60)), None);
        Assert.Equal(StorageUrlStatus.Signed, emulator.Status);
        Assert.Equal("http", new Uri(emulator.Url!).Scheme);
        Assert.DoesNotContain(FakeSecret, emulator.Url);
    }

    [Theory]
    [InlineData("other-bucket", "installations/u/a.jpg")]
    [InlineData(Bucket, "/installations/u/a.jpg")]
    [InlineData(Bucket, "installations/../contracts/a.pdf")]
    [InlineData(Bucket, "")]
    public async Task S3_signer_refuses_locations_outside_the_allowed_scope(string bucket, string key)
    {
        using var signer = RealSigner(Options());
        var result = await signer.CreateReadUrlAsync(new StorageObjectLocation(bucket, key),
            new StorageUrlRequest(StorageUrlDisposition.Inline, "a.jpg", FilePolicies.Jpeg, TimeSpan.FromSeconds(60)), None);
        Assert.Equal(StorageUrlStatus.Unavailable, result.Status);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task Disabled_signer_answers_not_configured()
    {
        var signer = new DisabledStorageUrlSigner();
        Assert.False(signer.IsConfigured);
        Assert.Equal(StorageUrlStatus.NotConfigured, (await signer.CreateReadUrlAsync(new StorageObjectLocation(Bucket, EvidenceKey()),
            new StorageUrlRequest(StorageUrlDisposition.Inline, "a.jpg", FilePolicies.Jpeg, TimeSpan.FromSeconds(60)), None)).Status);
    }

    // ------------------------------------------------------------------ Content-Disposition

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("..\\..\\etc/passwd", "passwd.jpg")]
    [InlineData("a\"b\r\nSet-Cookie: x=1;.jpg", "abSet-Cookie: x=1.jpg")]
    [InlineData("   ", "file.jpg")]
    [InlineData("report.pdf", "report.pdf.jpg")]
    public void File_names_are_reduced_to_a_safe_base_name_with_the_verified_extension(string original, string expected)
        => Assert.Equal(expected, ContentDispositionHeader.SafeFileName(original, FilePolicies.Jpeg));

    [Fact]
    public void Content_disposition_has_an_ascii_fallback_and_an_rfc5987_utf8_name()
    {
        var header = ContentDispositionHeader.Build(StorageUrlDisposition.Attachment, "Mái nhà \"số 1\".jpg");
        Assert.Equal("attachment; filename=\"M_i nh_ s_ 1.jpg\"; filename*=UTF-8''M%C3%A1i%20nh%C3%A0%20s%E1%BB%91%201.jpg", header);
        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.StartsWith("inline; ", ContentDispositionHeader.Build(StorageUrlDisposition.Inline, "x.png"));
    }

    // ------------------------------------------------------------------ FileReadUrlService scope checks

    private static (FileReadUrlService Service, FakeStorageUrlSigner Signer, FakeStorageObjectVerifier Storage, FixedTimeProvider Clock) Service(StorageOptions? options = null)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var signer = new FakeStorageUrlSigner(clock);
        var storage = new FakeStorageObjectVerifier();
        return (new FileReadUrlService(signer, storage, options ?? Options(), clock, NullLogger<FileReadUrlService>.Instance), signer, storage, clock);
    }

    private static StoredFile VerifiedEvidence(FakeStorageObjectVerifier storage, string? key = null)
    {
        key ??= EvidenceKey();
        var version = storage.Put(Bucket, key, FakeStorageObjectVerifier.JpegBytes);
        return new StoredFile
        {
            Id = Guid.NewGuid(), Purpose = FilePurposes.InstallationEvidence, Provider = "S3", Bucket = Bucket, ObjectKey = key, OriginalFileName = "roof.jpg",
            DeclaredContentType = FilePolicies.Jpeg, DeclaredSizeBytes = FakeStorageObjectVerifier.JpegBytes.Length, ObjectVersionId = version,
            VerifiedContentType = FilePolicies.Jpeg, VerifiedSizeBytes = FakeStorageObjectVerifier.JpegBytes.Length,
            VerifiedChecksumSha256 = FakeStorageObjectVerifier.Sha256(FakeStorageObjectVerifier.JpegBytes), Status = FileStatuses.Verified,
            UploadedByUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task Verified_files_are_signed_pinned_to_their_version_after_a_head_check_with_the_capped_ttl()
    {
        var (service, signer, storage, clock) = Service(Options(ttl: 90));
        var file = VerifiedEvidence(storage);

        var result = await service.CreateAsync(file, FilePurposes.InstallationEvidence, StorageUrlDisposition.Attachment, TimeSpan.FromHours(2), None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(clock.GetUtcNow().AddSeconds(90), result.Value!.ExpiresAt);
        Assert.Equal(FilePolicies.Jpeg, result.Value.ContentType);
        Assert.Equal("roof.jpg", result.Value.FileName);
        var (location, request) = Assert.Single(signer.Calls);
        Assert.Equal(new StorageObjectLocation(Bucket, file.ObjectKey, file.ObjectVersionId), location);
        Assert.Equal(TimeSpan.FromSeconds(90), request.TimeToLive);
        Assert.Equal(StorageUrlDisposition.Attachment, request.Disposition);
        Assert.Equal(1, storage.HeadCalls);
        Assert.Equal(0, storage.Calls);

        // A shorter per-request lifetime is honoured.
        var shorter = await service.CreateAsync(file, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, TimeSpan.FromSeconds(45), None);
        Assert.Equal(clock.GetUtcNow().AddSeconds(45), shorter.Value!.ExpiresAt);
    }

    [Fact]
    public async Task Unverified_wrong_purpose_foreign_bucket_and_out_of_prefix_keys_are_never_signed()
    {
        var (service, signer, storage, _) = Service();

        var pending = VerifiedEvidence(storage);
        pending.Status = FileStatuses.PendingVerification;
        var rejected = VerifiedEvidence(storage);
        rejected.Status = FileStatuses.Rejected;
        var wrongPurpose = VerifiedEvidence(storage);
        wrongPurpose.Purpose = FilePurposes.SignedContract;
        var foreignBucket = VerifiedEvidence(storage);
        foreignBucket.Bucket = "someone-elses-bucket";
        var outsidePrefix = VerifiedEvidence(storage, $"contracts/signed/{Guid.NewGuid()}/x.jpg");
        var traversal = VerifiedEvidence(storage, $"installations/{Guid.NewGuid()}/../../contracts/x.jpg");
        var absolute = VerifiedEvidence(storage);
        absolute.ObjectKey = "/installations/" + absolute.ObjectKey;
        var wrongType = VerifiedEvidence(storage);
        wrongType.VerifiedContentType = "text/html";

        Assert.Equal(FileErrorCodes.NotVerified, (await service.CreateAsync(pending, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotVerified, (await service.CreateAsync(rejected, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        foreach (var file in new[] { wrongPurpose, foreignBucket, outsidePrefix, traversal, absolute, wrongType })
            Assert.Equal(FileErrorCodes.NotSignable, (await service.CreateAsync(file, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        Assert.Empty(signer.Calls);
        Assert.Equal(0, storage.HeadCalls);
        Assert.Equal(409, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(FileErrorCodes.NotSignable));
    }

    [Fact]
    public async Task Missing_objects_outages_and_an_unconfigured_signer_map_to_their_codes()
    {
        var (service, signer, storage, _) = Service();
        var file = VerifiedEvidence(storage);

        storage.DeleteVersion(Bucket, file.ObjectKey, file.ObjectVersionId!);
        Assert.Equal(FileErrorCodes.ObjectMissing, (await service.CreateAsync(file, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);

        var other = VerifiedEvidence(storage);
        storage.Unavailable = true;
        Assert.Equal(StorageErrorCodes.Unavailable, (await service.CreateAsync(other, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        storage.Unavailable = false;

        signer.Outcome = StorageUrlStatus.Unavailable;
        Assert.Equal(StorageErrorCodes.Unavailable, (await service.CreateAsync(other, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        signer.Outcome = null;

        signer.IsConfigured = false;
        var heads = storage.HeadCalls;
        Assert.Equal(StorageErrorCodes.NotConfigured, (await service.CreateAsync(other, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).ErrorCode);
        Assert.Equal(heads, storage.HeadCalls);

        Assert.Equal(410, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(FileErrorCodes.ObjectMissing));
        Assert.Equal(503, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(StorageErrorCodes.Unavailable));
        Assert.Equal(503, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(StorageErrorCodes.NotConfigured));
    }

    [Fact]
    public async Task The_head_check_can_be_switched_off()
    {
        var (service, signer, storage, _) = Service(Options(verifyFirst: false));
        var file = VerifiedEvidence(storage);
        storage.Delete(Bucket, file.ObjectKey);

        Assert.True((await service.CreateAsync(file, FilePurposes.InstallationEvidence, StorageUrlDisposition.Inline, null, None)).Succeeded);
        Assert.Equal(0, storage.HeadCalls);
        Assert.Single(signer.Calls);
    }
}
