using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Infrastructure.Storage;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Files;

/// <summary>Runs only against a disposable local S3-compatible server (MinIO): <c>SMARTSOLAR_TEST_S3=endpoint|accessKey|secretKey[|browserOrigin]</c>.</summary>
public sealed class S3FactAttribute : FactAttribute
{
    public const string Variable = "SMARTSOLAR_TEST_S3";

    public S3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to 'http://127.0.0.1:9000|accessKey|secretKey' of a disposable local MinIO to run.";
    }
}

/// <summary>
/// The whole upload chain against a real S3-compatible server over HTTP, exactly as the browser drives it: POST presigned-upload
/// (real SigV4 presigner) → PUT the bytes to the signed URL with the signed Content-Type → POST /api/files registration, which verifies the
/// object (HEAD, magic bytes, size, SHA-256, version id). Covers JPG, PNG, WebP and PDF, the storage-side failures the frontend reports
/// (403 for a wrong Content-Type or an expired link, CORS preflight), the verification rejections (wrong MIME, size mismatch, too large)
/// and that an unverified file cannot be attached to an on-site survey. Nothing here touches AWS or any shared bucket.
/// </summary>
public sealed class S3CompatibleUploadChainTests
{
    private const string Bucket = "smartsolar-upload-chain-test";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly byte[] WebpBytes =
        [0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20, 0x0E, 0x00, 0x00, 0x00, 0x30, 0x01, 0x00, 0x9D, 0x01, 0x2A];

    private sealed record Chain(InstallationWorkflowTestContext Ctx, IAmazonS3 Client, StorageOptions Options, S3StorageUploadUrlSigner Signer,
        FileUploadUrlService Uploads, FileService Files, HttpClient Http, string Origin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            Client.Dispose();
            await Ctx.DisposeAsync();
        }
    }

    private static async Task<Chain> StartAsync()
    {
        var parts = Environment.GetEnvironmentVariable(S3FactAttribute.Variable)!.Split('|');
        var endpoint = parts[0].Trim();
        if (!endpoint.Contains("127.0.0.1", StringComparison.Ordinal) && !endpoint.Contains("localhost", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to run the S3 chain test against a non-local endpoint.");
        var origin = parts.Length > 3 ? parts[3].Trim() : "http://localhost:3000";
        var options = new StorageOptions
        {
            DefaultBucket = Bucket,
            S3 = new S3VerificationOptions
            {
                Enabled = true, ServiceUrl = endpoint, ForcePathStyle = true, Region = "us-east-1", AccessKeyId = parts[1].Trim(), SecretAccessKey = parts[2].Trim(),
                RequireObjectVersioning = true, PresignedUrlTtlSeconds = 300, TimeoutSeconds = 15,
            },
        };
        var client = new AmazonS3Client(new BasicAWSCredentials(options.S3.AccessKeyId, options.S3.SecretAccessKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1", MaxErrorRetry = 1 });
        // The bucket the production configuration requires: private, versioning enabled (verified files are pinned to a version id).
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = Bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });

        var ctx = InstallationWorkflowTestContext.Create();
        var signer = new S3StorageUploadUrlSigner(client, options, TimeProvider.System, NullLogger<S3StorageUploadUrlSigner>.Instance);
        var verifier = new S3StorageObjectVerifier(client, options, NullLogger<S3StorageObjectVerifier>.Instance);
        var uploads = new FileUploadUrlService(signer, options, TimeProvider.System, NullLogger<FileUploadUrlService>.Instance);
        var files = new FileService(new FileStore(ctx.Db), verifier, options, ctx.Clock, NullLogger<FileService>.Instance);
        return new Chain(ctx, client, options, signer, uploads, files, new HttpClient(), origin);
    }

    /// <summary>What the frontend does: ask for the URL, PUT the bytes with exactly the returned headers, register the key.</summary>
    private static async Task<(HttpStatusCode Put, OperationResult<FileView>? Registered)> UploadAsync(Chain c, Guid technician, string purpose, string fileName,
        string contentType, byte[] bytes, long? declaredSize = null, string? putContentType = null)
    {
        var url = await c.Uploads.CreateAsync(new CreateUploadUrlCommand(technician, role => role == RoleCodes.Technician, purpose, fileName, contentType, declaredSize ?? bytes.Length), None);
        Assert.True(url.Succeeded, url.Message);
        var target = url.Value!;
        using var request = new HttpRequestMessage(HttpMethod.Put, target.UploadUrl) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(putContentType ?? target.Headers["Content-Type"]);
        using var put = await c.Http.SendAsync(request);
        if (!put.IsSuccessStatusCode) return (put.StatusCode, null);
        var registered = await c.Files.RegisterAsync(new RegisterFileCommand(technician, purpose, target.Bucket, target.ObjectKey, fileName, contentType, declaredSize ?? bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), None);
        return (put.StatusCode, registered);
    }

    [S3Fact]
    public async Task Valid_jpg_png_webp_and_pdf_go_through_presign_put_register_and_come_back_verified_and_version_pinned()
    {
        await using var c = await StartAsync();
        var technician = (await c.Ctx.Scheduling.SeedTechnicianAsync("Chain technician", null)).Id;

        var cases = new (string Name, string Type, byte[] Bytes)[]
        {
            ("roof.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes),
            ("roof.png", FilePolicies.Png, FakeStorageObjectVerifier.PngBytes),
            ("roof.webp", FilePolicies.Webp, WebpBytes),
            ("plan.pdf", FilePolicies.Pdf, FakeStorageObjectVerifier.PdfBytes),
        };
        foreach (var (name, type, bytes) in cases)
        {
            var (put, registered) = await UploadAsync(c, technician, FilePurposes.OnSiteSurveyEvidence, name, type, bytes);
            Assert.Equal(HttpStatusCode.OK, put);
            Assert.True(registered!.Succeeded, registered.Message);
            var view = registered.Value!;
            Assert.Equal((FileStatuses.Verified, type, (long)bytes.Length), (view.Status, view.ContentType, view.SizeBytes));
            Assert.False(string.IsNullOrEmpty(view.ObjectVersionId), $"{name}: the verified object must be pinned to a version id");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), view.ChecksumSha256);
            Assert.StartsWith($"onsite-surveys/{technician:D}/", view.ObjectKey);
        }
    }

    [S3Fact]
    public async Task Storage_answers_403_for_a_changed_content_type_or_an_expired_link_and_the_preflight_carries_cors_headers()
    {
        await using var c = await StartAsync();
        var technician = (await c.Ctx.Scheduling.SeedTechnicianAsync("Chain technician", null)).Id;

        // The Content-Type is part of the signature: a different one is refused by storage, as the frontend reports ("link invalid").
        var (wrongType, _) = await UploadAsync(c, technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes,
            putContentType: FilePolicies.Pdf);
        Assert.Equal(HttpStatusCode.Forbidden, wrongType);

        // An expired link is refused too (the service's own lifetime is 30–3600 s; the signer is asked for 1 s here).
        var key = FileUploadUrlService.NewObjectKey(FilePolicies.ByPurpose[FilePurposes.OnSiteSurveyEvidence], technician, FilePolicies.Jpeg);
        var expiring = await c.Signer.CreateUploadUrlAsync(new StorageObjectLocation(Bucket, key), FilePolicies.Jpeg, TimeSpan.FromSeconds(1), None);
        Assert.Equal(StorageUrlStatus.Signed, expiring.Status);
        // Expiry is decided by the storage server's clock, not the test host's: a fixed host-side sleep made this check depend on the drift
        // between macOS and the Docker VM (it failed once in a long full run). Read the signed validity from the URL itself, check that the
        // requested 1 s lifetime is what was signed, and wait (bounded) until the SERVER's own clock is past it. The assertion is unchanged.
        var (signedAt, lifetime) = SignedValidity(expiring.Url!);
        Assert.InRange(lifetime.TotalSeconds, 1, 2);
        await WaitUntilServerClockPassesAsync(c, signedAt + lifetime);
        using var late = new HttpRequestMessage(HttpMethod.Put, expiring.Url) { Content = new ByteArrayContent(FakeStorageObjectVerifier.JpegBytes) };
        late.Content.Headers.ContentType = new MediaTypeHeaderValue(FilePolicies.Jpeg);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.SendAsync(late)).StatusCode);

        // The browser's preflight for the PUT (Origin + content-type): the server must answer with CORS headers, otherwise the browser
        // never sends the PUT and axios reports status 0 ("Không tải được tệp lên. Mạng vẫn ổn thì báo quản trị hệ thống.").
        var fresh = await c.Uploads.CreateAsync(new CreateUploadUrlCommand(technician, role => role == RoleCodes.Technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg",
            FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes.Length), None);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, fresh.Value!.UploadUrl);
        preflight.Headers.Add("Origin", c.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var answer = await c.Http.SendAsync(preflight);
        Assert.True(answer.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed), $"preflight {(int)answer.StatusCode} without Access-Control-Allow-Origin");
        Assert.Contains(allowed, o => o == "*" || string.Equals(o, c.Origin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>X-Amz-Date and X-Amz-Expires of a SigV4 presigned URL.</summary>
    private static (DateTimeOffset SignedAt, TimeSpan Lifetime) SignedValidity(string url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var signedAt = DateTimeOffset.ParseExact(query["X-Amz-Date"]!, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        return (signedAt, TimeSpan.FromSeconds(int.Parse(query["X-Amz-Expires"]!, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Polls the storage server's own clock (the <c>Date</c> header, whole seconds) until it is strictly after <paramref name="expiresAt"/>.
    /// Fails after 30 s rather than asserting on a link the server may still consider valid.
    /// </summary>
    private static async Task WaitUntilServerClockPassesAsync(Chain c, DateTimeOffset expiresAt)
    {
        var endpoint = Environment.GetEnvironmentVariable(S3FactAttribute.Variable)!.Split('|')[0].Trim().TrimEnd('/');
        var giveUp = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            using var probe = await c.Http.GetAsync($"{endpoint}/minio/health/live");
            if (probe.Headers.Date is { } serverNow && serverNow > expiresAt.AddSeconds(1)) return;
            if (DateTimeOffset.UtcNow > giveUp)
                Assert.Fail($"The storage server's clock ({probe.Headers.Date:O}) did not pass the link expiry {expiresAt:O} within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    [S3Fact]
    public async Task Verification_rejects_a_wrong_mime_a_size_mismatch_and_an_oversize_declaration_and_only_verified_files_attach()
    {
        await using var c = await StartAsync();
        var survey = await c.Ctx.ConfirmedSurveyAsync();
        var technician = survey.Technician.Id;

        // Declared JPEG, PNG bytes: the stored content decides.
        var (_, wrongMime) = await UploadAsync(c, technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.PngBytes);
        Assert.Equal(FileStatuses.Rejected, wrongMime!.Value!.Status);
        Assert.StartsWith("The stored content does not match an allowed file type", wrongMime.Value.RejectionReason);
        // Declared 100 bytes, uploaded 14: an interrupted or different upload.
        var (_, sizeMismatch) = await UploadAsync(c, technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes, declaredSize: 100);
        Assert.Equal(FileStatuses.Rejected, sizeMismatch!.Value!.Status);
        Assert.StartsWith("The stored object size does not match", sizeMismatch.Value.RejectionReason);
        // Over the purpose's limit: refused before any URL is signed.
        var oversize = await c.Uploads.CreateAsync(new CreateUploadUrlCommand(technician, role => role == RoleCodes.Technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg",
            FilePolicies.Jpeg, 15L * 1024 * 1024 + 1), None);
        Assert.Equal(FileErrorCodes.TooLarge, oversize.ErrorCode);
        // A role that never uploads this purpose gets nothing either.
        var customer = await c.Uploads.CreateAsync(new CreateUploadUrlCommand(technician, role => role == RoleCodes.Customer, FilePurposes.OnSiteSurveyEvidence, "roof.jpg",
            FilePolicies.Jpeg, 10), None);
        Assert.Equal(FileErrorCodes.UploadNotAllowed, customer.ErrorCode);

        // Only a VERIFIED file attaches to the survey; the ROOF photo it needs for completion is a verified one.
        c.Ctx.Clock.Now = At(10).ToUniversalTime();
        var onSite = new OnSiteSurveyHandlers(new OnSiteSurveyUnitOfWork(c.Ctx.Db), c.Files, c.Ctx.Scheduling.OnSiteOptions, c.Ctx.Scheduling.Policies, c.Ctx.Clock);
        Assert.True((await onSite.StartAsync(technician, survey.AppointmentId, None)).Succeeded);
        var refused = await onSite.AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(technician, survey.AppointmentId, wrongMime.Value.FileId, "ROOF", null), None);
        Assert.Equal(FileErrorCodes.NotVerified, refused.ErrorCode);
        var (_, good) = await UploadAsync(c, technician, FilePurposes.OnSiteSurveyEvidence, "roof.jpg", FilePolicies.Jpeg, FakeStorageObjectVerifier.JpegBytes);
        var attached = await onSite.AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(technician, survey.AppointmentId, good!.Value!.FileId, "ROOF", "South side"), None);
        Assert.True(attached.Succeeded, attached.Message);
        Assert.Contains(attached.Value!.Attachments, a => a.FileId == good.Value.FileId && a.Category == "ROOF");
    }
}
