using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Files;

/// <summary>
/// Catalog pictures uploaded from a staff device through the real pipeline: the normal presigned upload (PRODUCT_IMAGE / PACKAGE_IMAGE), the
/// product gallery and package endpoints accepting a <c>fileId</c>, and the anonymous <c>GET /api/files/public/{fileId}</c> redirect that lets
/// visitors load the picture while the bucket stays private. Storage and the presigners are synthetic.
/// </summary>
public sealed class PublicFileEndpointsTests
{
    internal const string PublicBase = "https://app.smartsolar.test";

    internal sealed record Api(AuthApiFactory Factory, FakeStorageObjectVerifier Storage) : IDisposable
    {
        public void Dispose() => Factory.Dispose();

        /// <summary>A visitor without a token that does not follow redirects (the Location header is the assertion).</summary>
        public HttpClient Visitor() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    internal static Api Start(string? publicBase = PublicBase)
    {
        var storage = new FakeStorageObjectVerifier();
        var factory = Enabled(storage, new FakeRagServiceClient(), extra: new() { ["Storage:PublicFileBaseUrl"] = publicBase ?? "" }, services: services =>
        {
            services.RemoveAll<IStorageUrlSigner>();
            services.AddSingleton<IStorageUrlSigner>(new FakeStorageUrlSigner());
            services.RemoveAll<IStorageUploadUrlSigner>();
            services.AddSingleton<IStorageUploadUrlSigner>(new FakeStorageUploadUrlSigner());
        });
        return new Api(factory, storage);
    }

    /// <summary>The frontend flow: presigned URL, PUT (simulated), register; returns the VERIFIED file id.</summary>
    internal static async Task<Guid> UploadPictureAsync(HttpClient client, FakeStorageObjectVerifier storage, string purpose)
    {
        var presign = await DataOf(await client.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose, fileName = "cover.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }));
        var objectKey = presign.GetProperty("objectKey").GetString()!;
        storage.Put(Bucket, objectKey, FakeStorageObjectVerifier.JpegBytes);
        var registered = await DataOf(await client.PostAsJsonAsync("/api/files",
            new { purpose, objectKey, fileName = "cover.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }), HttpStatusCode.Created);
        Assert.Equal(FileStatuses.Verified, registered.GetProperty("status").GetString());
        return registered.GetProperty("fileId").GetGuid();
    }

    private static Guid SeedProduct(AuthApiFactory factory, string sku)
    {
        using var db = factory.CreateDbContext();
        var product = PreSurveyTestContext.NewPanelProduct(sku);
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    internal static object PackageBody(string name, Guid? imageFileId = null, string? imageUrl = null, int revision = 0)
        => new { kind = "MAINTENANCE", name, description = "Two visits", price = 2_000_000m, durationMonths = 12, visitMonths = new[] { 6, 12 }, isActive = true, revision, imageFileId, imageUrl };

    [Fact]
    public async Task A_manager_uploads_a_product_picture_from_the_device_and_a_visitor_loads_it_through_the_public_redirect()
    {
        using var api = Start();
        var manager = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Manager);
        var productId = SeedProduct(api.Factory, "PNL-PUBLIC");
        var fileId = await UploadPictureAsync(manager, api.Storage, FilePurposes.ProductImage);

        var added = await DataOf(await manager.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { fileId, altText = "Front" }), HttpStatusCode.Created);
        var image = Assert.Single(added.GetProperty("images").EnumerateArray());
        var expectedUrl = $"{PublicBase}/api/files/public/{fileId:D}";
        Assert.Equal((expectedUrl, true, "Front"), (image.GetProperty("url").GetString(), image.GetProperty("isPrimary").GetBoolean(), image.GetProperty("altText").GetString()));
        // The public catalog shows the same address.
        var catalog = await DataOf(await api.Visitor().GetAsync($"/api/products/{productId}"));
        Assert.Equal(expectedUrl, catalog.GetProperty("imageUrl").GetString());

        // A visitor (no token) is redirected to a short-lived presigned URL; browsers may cache the redirect briefly.
        var redirect = await api.Visitor().GetAsync($"/api/files/public/{fileId}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.StartsWith(FakeStorageUrlSigner.Host, redirect.Headers.Location!.ToString());
        Assert.Contains("products/", redirect.Headers.Location.ToString());
        Assert.True(redirect.Headers.CacheControl?.Public);
        // The same file cannot be added twice (same URL), and a second picture of the same product is not primary.
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { fileId }), HttpStatusCode.Conflict, "PRODUCT_IMAGE_DUPLICATE");
    }

    [Fact]
    public async Task Uploading_catalog_pictures_is_limited_to_the_catalog_roles_and_the_public_read_serves_only_verified_pictures()
    {
        using var api = Start();
        var technician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician);
        var manager = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Manager);
        var adminId = Guid.NewGuid();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin, adminId);
        var productId = SeedProduct(api.Factory, "PNL-ROLES");

        // Roles: product pictures by Admin/Manager, package covers by Admin only.
        await AssertFailureAsync(await technician.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.ProductImage, fileName = "a.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 10 }), HttpStatusCode.Forbidden, FileErrorCodes.UploadNotAllowed);
        await AssertFailureAsync(await manager.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PackageImage, fileName = "a.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 10 }), HttpStatusCode.Forbidden, FileErrorCodes.UploadNotAllowed);
        // A PDF is not a picture.
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.ProductImage, fileName = "a.pdf", contentType = FilePolicies.Pdf, sizeBytes = 10 }), HttpStatusCode.BadRequest, FileErrorCodes.TypeNotAllowed);

        // Only verified PRODUCT_IMAGE / PACKAGE_IMAGE files are served publicly; a private document or an unknown id is 404 without disclosure.
        var document = await UploadAsync(admin, api.Storage, adminId, FilePurposes.KnowledgeDocument);
        await AssertFailureAsync(await api.Visitor().GetAsync($"/api/files/public/{document}"), HttpStatusCode.NotFound, FileErrorCodes.NotFound);
        await AssertFailureAsync(await api.Visitor().GetAsync($"/api/files/public/{Guid.NewGuid()}"), HttpStatusCode.NotFound, FileErrorCodes.NotFound);
        // A document cannot be attached as a product picture, and url + fileId together are refused.
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { fileId = document }), HttpStatusCode.BadRequest, FileErrorCodes.WrongPurpose);
        var picture = await UploadPictureAsync(admin, api.Storage, FilePurposes.ProductImage);
        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { fileId = picture, url = "https://cdn.example.com/a.jpg" }),
            HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);   // the catalog's validation envelope
    }

    [Fact]
    public async Task Without_a_public_base_url_device_uploads_answer_503_while_https_links_keep_working()
    {
        using var api = Start(publicBase: null);
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var productId = SeedProduct(api.Factory, "PNL-NOBASE");
        var picture = await UploadPictureAsync(admin, api.Storage, FilePurposes.ProductImage);

        await AssertFailureAsync(await admin.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { fileId = picture }),
            HttpStatusCode.ServiceUnavailable, StorageErrorCodes.PublicUrlNotConfigured);
        var linked = await DataOf(await admin.PostAsJsonAsync($"/api/admin/products/{productId}/images", new { url = "https://cdn.example.com/a.jpg" }), HttpStatusCode.Created);
        Assert.Equal("https://cdn.example.com/a.jpg", Assert.Single(linked.GetProperty("images").EnumerateArray()).GetProperty("url").GetString());
        var cover = await UploadPictureAsync(admin, api.Storage, FilePurposes.PackageImage);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care", imageFileId: cover)),
            HttpStatusCode.ServiceUnavailable, StorageErrorCodes.PublicUrlNotConfigured);
    }

    [Fact]
    public async Task A_package_cover_comes_from_an_uploaded_file_or_an_https_link_and_customers_see_it()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer);
        var cover = await UploadPictureAsync(admin, api.Storage, FilePurposes.PackageImage);

        var created = await DataOf(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care 12", imageFileId: cover)), HttpStatusCode.Created);
        var packageId = created.GetProperty("packageId").GetGuid();
        var expectedUrl = $"{PublicBase}/api/files/public/{cover:D}";
        Assert.Equal(expectedUrl, created.GetProperty("imageUrl").GetString());
        var listed = await DataOf(await customer.GetAsync("/api/customers/me/after-sales/packages?kind=MAINTENANCE"));
        Assert.Equal(expectedUrl, listed.EnumerateArray().Single(p => p.GetProperty("packageId").GetGuid() == packageId).GetProperty("imageUrl").GetString());
        Assert.Equal(HttpStatusCode.Found, (await api.Visitor().GetAsync($"/api/files/public/{cover}")).StatusCode);

        // Validation: a non-https link is refused; both a link and a file are refused; an https link replaces the upload; no picture at all is fine.
        var revision = created.GetProperty("revision").GetInt32();
        await AssertFailureAsync(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{packageId}", PackageBody("Care 12", imageUrl: "http://cdn.example.com/c.jpg", revision: revision)),
            HttpStatusCode.BadRequest, "AFTER_SALES_VALIDATION_FAILED");
        await AssertFailureAsync(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{packageId}", PackageBody("Care 12", imageFileId: cover, imageUrl: "https://cdn.example.com/c.jpg", revision: revision)),
            HttpStatusCode.BadRequest, "AFTER_SALES_VALIDATION_FAILED");
        var linked = await DataOf(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{packageId}", PackageBody("Care 12", imageUrl: "https://cdn.example.com/c.jpg", revision: revision)));
        Assert.Equal("https://cdn.example.com/c.jpg", linked.GetProperty("imageUrl").GetString());
        var cleared = await DataOf(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{packageId}", PackageBody("Care 12", revision: linked.GetProperty("revision").GetInt32())));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("imageUrl").ValueKind);
        // The product-image file cannot be used as a package cover (wrong purpose).
        var productPicture = await UploadPictureAsync(admin, api.Storage, FilePurposes.ProductImage);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care 24", imageFileId: productPicture)), HttpStatusCode.BadRequest, FileErrorCodes.WrongPurpose);
    }
}
