using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Files;

/// <summary>Step 1 of an upload (presigned PUT URL): role per purpose, file policy checks, generated key and storage outcomes. The signer is synthetic.</summary>
public sealed class FileUploadUrlServiceTests
{
    private const string Bucket = "smartsolar-test";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid UserId = Guid.NewGuid();

    private static (FileUploadUrlService Service, FakeStorageUploadUrlSigner Signer) Create(string defaultBucket = Bucket)
    {
        var signer = new FakeStorageUploadUrlSigner();
        var options = new StorageOptions { DefaultBucket = defaultBucket };
        return (new FileUploadUrlService(signer, options, TimeProvider.System, NullLogger<FileUploadUrlService>.Instance), signer);
    }

    private static CreateUploadUrlCommand Command(string role, string purpose = FilePurposes.OnSiteSurveyEvidence, string fileName = "roof.jpg",
        string contentType = FilePolicies.Jpeg, long sizeBytes = 2_048_000)
        => new(UserId, r => r == role, purpose, fileName, contentType, sizeBytes);

    [Fact]
    public async Task A_technician_gets_a_put_url_for_a_new_key_under_the_purpose_prefix_and_own_user_id()
    {
        // Arrange
        var (service, signer) = Create();

        // Act
        var result = await service.CreateAsync(Command(RoleCodes.Technician), None);

        // Assert
        Assert.True(result.Succeeded, result.Message);
        var upload = result.Value!;
        Assert.Equal(("PUT", Bucket), (upload.Method, upload.Bucket));
        Assert.StartsWith($"onsite-surveys/{UserId:D}/", upload.ObjectKey);
        Assert.EndsWith(".jpg", upload.ObjectKey);
        Assert.Equal(FilePolicies.Jpeg, upload.Headers["Content-Type"]);
        Assert.StartsWith(FakeStorageUploadUrlSigner.Host, upload.UploadUrl);
        Assert.True(upload.ExpiresAt > DateTimeOffset.UtcNow);
        var call = Assert.Single(signer.Calls);
        Assert.Equal((Bucket, upload.ObjectKey, FilePolicies.Jpeg), (call.Location.Bucket, call.Location.ObjectKey, call.ContentType));
        // The generated key is exactly what POST /api/files accepts for this user.
        Assert.Null(FilePolicies.ValidateKey(FilePolicies.ByPurpose[FilePurposes.OnSiteSurveyEvidence], upload.ObjectKey, UserId));
    }

    [Fact]
    public async Task Two_requests_get_two_different_keys()
    {
        var (service, _) = Create();

        var first = await service.CreateAsync(Command(RoleCodes.Technician), None);
        var second = await service.CreateAsync(Command(RoleCodes.Technician), None);

        Assert.NotEqual(first.Value!.ObjectKey, second.Value!.ObjectKey);
    }

    [Theory]
    [InlineData(RoleCodes.Technician, FilePurposes.InstallationEvidence, "installations/")]
    [InlineData(RoleCodes.Technician, FilePurposes.ServiceEvidence, "after-sales/")]
    [InlineData(RoleCodes.Customer, FilePurposes.ServiceEvidence, "after-sales/")]
    public async Task Each_flow_uses_its_own_prefix(string role, string purpose, string prefix)
    {
        var (service, _) = Create();

        var result = await service.CreateAsync(Command(role, purpose), None);

        Assert.True(result.Succeeded, result.Message);
        Assert.StartsWith($"{prefix}{UserId:D}/", result.Value!.ObjectKey);
    }

    [Theory]
    [InlineData(RoleCodes.Customer, FilePurposes.OnSiteSurveyEvidence)]
    [InlineData(RoleCodes.Customer, FilePurposes.InstallationEvidence)]
    [InlineData(RoleCodes.Technician, FilePurposes.SignedContract)]
    [InlineData(RoleCodes.Sales, FilePurposes.KnowledgeDocument)]
    public async Task A_role_cannot_upload_a_purpose_it_never_attaches(string role, string purpose)
    {
        var (service, signer) = Create();

        var result = await service.CreateAsync(Command(role, purpose, "doc.pdf", FilePolicies.Pdf), None);

        Assert.Equal(FileErrorCodes.UploadNotAllowed, result.ErrorCode);
        Assert.Empty(signer.Calls);
    }

    [Theory]
    [InlineData("photo.heic", "image/heic", 1_000, FileErrorCodes.TypeNotAllowed)]
    [InlineData("roof.png", FilePolicies.Jpeg, 1_000, FileErrorCodes.TypeNotAllowed)]
    [InlineData("roof.jpg", FilePolicies.Jpeg, 15 * 1024 * 1024 + 1, FileErrorCodes.TooLarge)]
    [InlineData("roof.jpg", FilePolicies.Jpeg, 0, FileErrorCodes.TooLarge)]
    public async Task Files_outside_the_purpose_policy_are_refused(string fileName, string contentType, long sizeBytes, string expected)
    {
        var (service, signer) = Create();

        var result = await service.CreateAsync(Command(RoleCodes.Technician, fileName: fileName, contentType: contentType, sizeBytes: sizeBytes), None);

        Assert.Equal(expected, result.ErrorCode);
        Assert.Empty(signer.Calls);
    }

    [Fact]
    public async Task An_unknown_purpose_is_refused()
    {
        var (service, _) = Create();

        Assert.Equal(FileErrorCodes.InvalidPurpose, (await service.CreateAsync(Command(RoleCodes.Technician, purpose: "AVATAR"), None)).ErrorCode);
    }

    [Fact]
    public async Task Storage_problems_answer_not_configured_or_unavailable_without_a_url()
    {
        var (service, signer) = Create();

        signer.IsConfigured = false;
        Assert.Equal(StorageErrorCodes.NotConfigured, (await service.CreateAsync(Command(RoleCodes.Technician), None)).ErrorCode);
        signer.IsConfigured = true;
        signer.Outcome = StorageUrlStatus.Unavailable;
        Assert.Equal(StorageErrorCodes.Unavailable, (await service.CreateAsync(Command(RoleCodes.Technician), None)).ErrorCode);
        var (noBucket, _) = Create(defaultBucket: "");
        Assert.Equal(StorageErrorCodes.NotConfigured, (await noBucket.CreateAsync(Command(RoleCodes.Technician), None)).ErrorCode);
    }
}
