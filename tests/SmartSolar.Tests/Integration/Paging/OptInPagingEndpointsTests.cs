using System.Net;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Paging;

/// <summary>
/// PAGINATION through the API pipeline: each opt-in list keeps its historical array without paging parameters (existing frontend calls unchanged),
/// answers the repository's paged shape with them, refuses invalid values with 400 VALIDATION_FAILED, and keeps its role restriction.
/// </summary>
public sealed class OptInPagingEndpointsTests
{
    public static TheoryData<string, string> Lists => new()
    {
        { "/api/installation-projects", RoleCodes.Manager },
        { "/api/manager/installation-schedules", RoleCodes.Manager },
        { "/api/manager/after-sales/requests", RoleCodes.Manager },
        { "/api/customers/me/after-sales/requests", RoleCodes.Customer },
        { "/api/customers/me/after-sales/notifications", RoleCodes.Customer },
        // Second pass (2026-11-18).
        { "/api/survey-requests/pending", RoleCodes.Sales },
        { "/api/survey-requests/my", RoleCodes.Sales },
        { "/api/sales/after-sales/tasks/open", RoleCodes.Sales },
        { "/api/sales/after-sales/tasks/mine", RoleCodes.Sales },
        { "/api/manager/after-sales/tasks", RoleCodes.Manager },
        { "/api/technicians/me/after-sales/visits", RoleCodes.Technician },
        { "/api/admin/knowledge-documents", RoleCodes.Admin },
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task PAGINATION_API_Legacy_array_without_parameters_paged_object_with_them_and_400_for_invalid_values(string path, string role)
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var client = CatalogEndpointsTests.ClientFor(factory, role);

        var legacy = await DataOf(await client.GetAsync(path));
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind);

        var paged = await DataOf(await client.GetAsync($"{path}?page=1&pageSize=5"));
        Assert.Equal(JsonValueKind.Array, paged.GetProperty("items").ValueKind);
        Assert.Equal((1, 5, 0, 0), (paged.GetProperty("page").GetInt32(), paged.GetProperty("pageSize").GetInt32(),
            paged.GetProperty("totalItems").GetInt32(), paged.GetProperty("totalPages").GetInt32()));
        // Only pageSize given: first page.
        Assert.Equal(1, (await DataOf(await client.GetAsync($"{path}?pageSize=10"))).GetProperty("page").GetInt32());

        foreach (var query in new[] { "page=0", "page=-3", "page=10001", "pageSize=0", "pageSize=101", "page=1&pageSize=1000" })
            await AssertFailureAsync(await client.GetAsync($"{path}?{query}"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task PAGINATION_API_Paging_does_not_open_a_list_to_other_roles(string path, string role)
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        foreach (var other in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
        {
            var allowed = other == role || (role == RoleCodes.Manager && other == RoleCodes.Admin);
            var status = (await CatalogEndpointsTests.ClientFor(factory, other).GetAsync($"{path}?page=1&pageSize=5")).StatusCode;
            Assert.True(allowed ? status == HttpStatusCode.OK : status == HttpStatusCode.Forbidden, $"{other} {path}: {(int)status}");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"{path}?page=1")).StatusCode);
    }
}
