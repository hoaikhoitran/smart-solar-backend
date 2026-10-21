using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Files;

/// <summary>
/// The frontend upload flow through the real pipeline: POST /api/files/presigned-upload, PUT to storage (synthetic object store),
/// then POST /api/files, which verifies the object. Storage and the presigner are synthetic; nothing calls AWS.
/// </summary>
public sealed class FileUploadEndpointsTests
{
    private sealed record Api(AuthApiFactory Factory, FakeStorageObjectVerifier Storage, FakeStorageUploadUrlSigner Signer) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }

    private static Api Start(bool s3Configured = true)
    {
        var storage = new FakeStorageObjectVerifier();
        var signer = new FakeStorageUploadUrlSigner();
        var factory = Enabled(storage, new FakeRagServiceClient(), services: services =>
        {
            if (!s3Configured) return;   // keeps the real DisabledStorageUploadUrlSigner
            services.RemoveAll<IStorageUploadUrlSigner>();
            services.AddSingleton<IStorageUploadUrlSigner>(signer);
        });
        return new Api(factory, storage, signer);
    }

    private static object PhotoRequest(string purpose = FilePurposes.OnSiteSurveyEvidence, string fileName = "roof.jpg", string contentType = FilePolicies.Jpeg,
        long? sizeBytes = null)
        => new { purpose, fileName, contentType, sizeBytes = sizeBytes ?? FakeStorageObjectVerifier.JpegBytes.Length };

    [Fact]
    public async Task A_technician_uploads_a_photo_end_to_end_and_the_registered_file_is_verified()
    {
        // Arrange
        using var api = Start();
        var technicianId = Guid.NewGuid();
        var technician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician, technicianId);

        // Act 1: ask for the upload URL.
        var presign = await technician.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest());
        var upload = await DataOf(presign);

        // Assert 1
        Assert.True(presign.Headers.CacheControl?.NoStore);
        Assert.Equal("PUT", upload.GetProperty("method").GetString());
        Assert.Equal(Bucket, upload.GetProperty("bucket").GetString());
        Assert.Equal(FilePolicies.Jpeg, upload.GetProperty("headers").GetProperty("Content-Type").GetString());
        var objectKey = upload.GetProperty("objectKey").GetString()!;
        Assert.StartsWith($"onsite-surveys/{technicianId:D}/", objectKey);
        Assert.StartsWith(FakeStorageUploadUrlSigner.Host, upload.GetProperty("uploadUrl").GetString());

        // Act 2: the browser PUTs the bytes (simulated), then registers the key.
        api.Storage.Put(Bucket, objectKey, FakeStorageObjectVerifier.JpegBytes);
        var registered = await DataOf(await technician.PostAsJsonAsync("/api/files", new
        {
            purpose = FilePurposes.OnSiteSurveyEvidence, objectKey, fileName = "roof.jpg", contentType = FilePolicies.Jpeg,
            sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length,
        }), HttpStatusCode.Created);

        // Assert 2
        Assert.Equal(FileStatuses.Verified, registered.GetProperty("status").GetString());
        var fileId = registered.GetProperty("fileId").GetGuid();
        var reread = await DataOf(await technician.GetAsync($"/api/files/{fileId}"));
        Assert.Equal(FileStatuses.Verified, reread.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Registering_a_key_that_was_never_uploaded_is_rejected_with_a_reason()
    {
        using var api = Start();
        var technician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician);
        var objectKey = (await DataOf(await technician.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest()))).GetProperty("objectKey").GetString();

        var registered = await DataOf(await technician.PostAsJsonAsync("/api/files", new
        {
            purpose = FilePurposes.OnSiteSurveyEvidence, objectKey, fileName = "roof.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 1000,
        }), HttpStatusCode.Created);

        // 201 only means "registered": the status says whether the file can be used.
        Assert.Equal(FileStatuses.Rejected, registered.GetProperty("status").GetString());
        Assert.Contains("does not exist", registered.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task Callers_need_a_token_and_a_role_that_uploads_the_purpose()
    {
        using var api = Start();

        var anonymous = await api.Factory.CreateClient().PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest());
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer);
        var forbidden = await customer.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest());
        var faultPhoto = await customer.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest(FilePurposes.ServiceEvidence));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AssertFailureAsync(forbidden, HttpStatusCode.Forbidden, FileErrorCodes.UploadNotAllowed);
        Assert.StartsWith("after-sales/", (await DataOf(faultPhoto)).GetProperty("objectKey").GetString());
    }

    [Fact]
    public async Task Invalid_files_and_missing_storage_return_clear_errors()
    {
        using var api = Start();
        var technician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician);

        await AssertFailureAsync(await technician.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest(fileName: "IMG_0001.heic", contentType: "image/heic")),
            HttpStatusCode.BadRequest, FileErrorCodes.TypeNotAllowed);
        await AssertFailureAsync(await technician.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest(sizeBytes: 16 * 1024 * 1024)),
            HttpStatusCode.BadRequest, FileErrorCodes.TooLarge);
        await AssertFailureAsync(await technician.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest(purpose: "AVATAR")),
            HttpStatusCode.BadRequest, FileErrorCodes.InvalidPurpose);

        using var disabled = Start(s3Configured: false);
        var other = CatalogEndpointsTests.ClientFor(disabled.Factory, RoleCodes.Technician);
        await AssertFailureAsync(await other.PostAsJsonAsync("/api/files/presigned-upload", PhotoRequest()),
            HttpStatusCode.ServiceUnavailable, StorageErrorCodes.NotConfigured);
    }
}
