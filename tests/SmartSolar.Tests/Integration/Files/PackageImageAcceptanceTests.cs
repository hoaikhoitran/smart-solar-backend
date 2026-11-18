using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using static SmartSolar.Tests.Integration.Files.PublicFileEndpointsTests;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Files;

/// <summary>
/// PKG-IMG acceptance scenarios (2026-11-18) through the real API pipeline. The feature is the existing one (Admin uploads a PACKAGE_IMAGE file
/// through the shared presigned flow, or gives an https link; <c>imageUrl</c> on the package views); these tests pin each requested scenario.
/// PKG-IMG-008 (purchase history unaffected) is at handler level in <c>PackageImageHistoryTests</c>.
/// </summary>
public sealed class PackageImageAcceptanceTests
{
    private static async Task<JsonElement> PackageOfAsync(HttpClient client, Guid packageId, string path = "/api/admin/after-sales/packages")
        => (await DataOf(await client.GetAsync(path))).EnumerateArray().Single(p => p.GetProperty("packageId").GetGuid() == packageId);

    [Fact]
    public async Task PKG_IMG_001_002_An_admin_attaches_an_uploaded_cover_and_both_admin_and_customer_lists_show_it()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer);
        var cover = await UploadPictureAsync(admin, api.Storage, FilePurposes.PackageImage);

        var created = await DataOf(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care 12", imageFileId: cover)), HttpStatusCode.Created);
        var id = created.GetProperty("packageId").GetGuid();
        var expected = $"{PublicBase}/api/files/public/{cover:D}";

        Assert.Equal(expected, created.GetProperty("imageUrl").GetString());
        Assert.Equal(expected, (await PackageOfAsync(admin, id)).GetProperty("imageUrl").GetString());
        Assert.Equal(expected, (await PackageOfAsync(customer, id, "/api/customers/me/after-sales/packages?kind=MAINTENANCE")).GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task PKG_IMG_003_004_An_admin_replaces_the_cover_with_another_upload_then_removes_it()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var first = await UploadPictureAsync(admin, api.Storage, FilePurposes.PackageImage);
        var second = await UploadPictureAsync(admin, api.Storage, FilePurposes.PackageImage);
        var created = await DataOf(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care 12", imageFileId: first)), HttpStatusCode.Created);
        var id = created.GetProperty("packageId").GetGuid();

        var replaced = await DataOf(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{id}",
            PackageBody("Care 12", imageFileId: second, revision: created.GetProperty("revision").GetInt32())));
        Assert.Equal($"{PublicBase}/api/files/public/{second:D}", replaced.GetProperty("imageUrl").GetString());

        var removed = await DataOf(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{id}",
            PackageBody("Care 12", revision: replaced.GetProperty("revision").GetInt32())));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("imageUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await PackageOfAsync(admin, id)).GetProperty("imageUrl").ValueKind);
        // A stale revision is refused (no lost update of the cover).
        await AssertFailureAsync(await admin.PutAsJsonAsync($"/api/admin/after-sales/packages/{id}",
            PackageBody("Care 12", imageFileId: first, revision: created.GetProperty("revision").GetInt32())), HttpStatusCode.Conflict, "AFTER_SALES_STALE_REVISION");
    }

    [Fact]
    public async Task PKG_IMG_005_A_non_picture_or_an_oversized_file_cannot_become_a_package_cover()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);

        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PackageImage, fileName = "cover.pdf", contentType = FilePolicies.Pdf, sizeBytes = 1000 }), HttpStatusCode.BadRequest, FileErrorCodes.TypeNotAllowed);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PackageImage, fileName = "cover.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 10L * 1024 * 1024 + 1 }), HttpStatusCode.BadRequest, FileErrorCodes.TooLarge);
        // An unknown or unverified file id is refused, as is a picture uploaded for another purpose.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care", imageFileId: Guid.NewGuid()))).StatusCode);
        var productPicture = await UploadPictureAsync(admin, api.Storage, FilePurposes.ProductImage);
        await AssertFailureAsync(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care", imageFileId: productPicture)),
            HttpStatusCode.BadRequest, FileErrorCodes.WrongPurpose);
        Assert.Empty((await DataOf(await admin.GetAsync("/api/admin/after-sales/packages"))).EnumerateArray());
    }

    [Fact]
    public async Task PKG_IMG_006_Only_an_admin_can_change_a_package_or_upload_its_cover()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var created = await DataOf(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Care 12")), HttpStatusCode.Created);
        var id = created.GetProperty("packageId").GetGuid();

        foreach (var role in new[] { RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
        {
            var client = CatalogEndpointsTests.ClientFor(api.Factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/admin/after-sales/packages/{id}",
                PackageBody("Care 12", imageUrl: "https://cdn.example.com/x.jpg", revision: created.GetProperty("revision").GetInt32()))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/files/presigned-upload",
                new { purpose = FilePurposes.PackageImage, fileName = "a.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 10 })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Factory.CreateClient().PutAsJsonAsync($"/api/admin/after-sales/packages/{id}", PackageBody("Care 12"))).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await PackageOfAsync(admin, id)).GetProperty("imageUrl").ValueKind);
    }

    [Fact]
    public async Task PKG_IMG_007_A_package_without_a_cover_stays_readable_by_admin_and_customer()
    {
        using var api = Start();
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer);
        var created = await DataOf(await admin.PostAsJsonAsync("/api/admin/after-sales/packages", PackageBody("Legacy care")), HttpStatusCode.Created);
        var id = created.GetProperty("packageId").GetGuid();

        var seen = await PackageOfAsync(customer, id, "/api/customers/me/after-sales/packages?kind=MAINTENANCE");
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("imageUrl").ValueKind);
        Assert.Equal(("Legacy care", 2_000_000m), (seen.GetProperty("name").GetString(), seen.GetProperty("price").GetDecimal()));
    }
}
