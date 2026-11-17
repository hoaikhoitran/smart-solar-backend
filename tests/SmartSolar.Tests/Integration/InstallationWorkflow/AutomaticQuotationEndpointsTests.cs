using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Automatic quotation endpoints through the real pipeline (authentication, role policies, envelope): only Sales, Manager or Admin can
/// trigger generation or read the review queue, and Technicians or Customers can never edit a quotation's prices.
/// </summary>
public sealed class AutomaticQuotationEndpointsTests
{
    private static string Generate(Guid surveyRequestId) => $"/api/survey-requests/{surveyRequestId}/quotations/generate";

    [Fact]
    public async Task Generation_and_the_review_queue_require_a_staff_role()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var anonymous = factory.CreateClient();
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Generate(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/quotations/pending-review")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsync(Generate(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync(Generate(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync("/api/quotations/pending-review")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/quotations/pending-review")).StatusCode);
    }

    /// <summary>2026-11-17: generation, the review queue and every Sales quotation write are retired (410) for every staff role.</summary>
    [Fact]
    public async Task Generation_the_review_queue_and_sales_quotation_writes_are_retired()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        foreach (var role in new[] { RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin })
        {
            var staff = CatalogEndpointsTests.ClientFor(factory, role);
            await AssertFailureAsync(await staff.PostAsync(Generate(id), null), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.GetAsync("/api/quotations/pending-review"), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PostAsJsonAsync($"/api/survey-requests/{id}/quotations", new { laborCost = 1m }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PutAsJsonAsync($"/api/quotations/{id}", new { revision = 1 }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PostAsJsonAsync($"/api/quotations/{id}/lines", new { revision = 1 }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PutAsJsonAsync($"/api/quotations/{id}/lines/{id}", new { revision = 1 }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.DeleteAsync($"/api/quotations/{id}/lines/{id}?revision=1"), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PostAsJsonAsync($"/api/quotations/{id}/issue", new { revision = 1 }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PostAsJsonAsync($"/api/quotations/{id}/confirm-survey-version", new { revision = 1, reason = "checked" }),
                HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PostAsync($"/api/quotations/{id}/revisions", null), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            await AssertFailureAsync(await staff.PutAsJsonAsync($"/api/quotations/{id}/document", new { fileId = id }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            // Reading stays: unknown quotation → 404.
            await AssertFailureAsync(await staff.GetAsync($"/api/quotations/{id}"), HttpStatusCode.NotFound, QuotationErrorCodes.NotFound);
        }
    }

    [Fact]
    public async Task Technicians_and_customers_cannot_change_quotation_prices_or_lines()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var quotationId = Guid.NewGuid();
        var header = new { laborCost = 1m, transportCost = 0m, otherServiceCost = 0m, discountAmount = 0m, taxRatePercent = 0m, revision = 1 };
        var line = new { productId = Guid.NewGuid(), quantity = 1m, unitPrice = 1m, revision = 1 };

        foreach (var role in new[] { RoleCodes.Technician, RoleCodes.Customer })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/quotations/{quotationId}", header)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/quotations/{quotationId}/lines", line)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/quotations/{quotationId}/issue", new { revision = 1, reviewNote = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"/api/quotations/{quotationId}/confirm-survey-version", new { revision = 1, reason = "x" })).StatusCode);
        }
    }

    [Fact]
    public async Task Energy_monitoring_is_customer_or_manager_only()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var projectId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/customers/me/installation-projects/{projectId}/energy-monitoring")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician).GetAsync($"/api/installation-projects/{projectId}/energy-monitoring")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync($"/api/installation-projects/{projectId}/energy-monitoring")).StatusCode);
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync($"/api/customers/me/installation-projects/{projectId}/energy-monitoring"),
            HttpStatusCode.NotFound, "INSTALLATION_PROJECT_NOT_FOUND");
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager).GetAsync($"/api/installation-projects/{projectId}/energy-monitoring"),
            HttpStatusCode.NotFound, "INSTALLATION_PROJECT_NOT_FOUND");
    }
}
