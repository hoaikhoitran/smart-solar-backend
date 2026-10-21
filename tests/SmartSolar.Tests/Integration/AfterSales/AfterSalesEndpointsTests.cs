using System.Net;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>Flow 3 endpoints through the real pipeline: role per area, Admin-only dashboard and CSV, feature gate.</summary>
public sealed class AfterSalesEndpointsTests
{
    [Fact]
    public async Task Each_after_sales_area_is_reachable_only_by_its_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        HttpClient As(string role) => CatalogEndpointsTests.ClientFor(factory, role);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/customers/me/after-sales/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(RoleCodes.Technician).GetAsync("/api/customers/me/after-sales/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(RoleCodes.Customer).GetAsync("/api/manager/after-sales/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(RoleCodes.Customer).GetAsync("/api/sales/after-sales/tasks/open")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(RoleCodes.Sales).GetAsync("/api/manager/after-sales/refunds")).StatusCode);
        // The dashboard is Admin only (a Manager may not see company money).
        Assert.Equal(HttpStatusCode.Forbidden, (await As(RoleCodes.Manager).GetAsync("/api/admin/after-sales/dashboard")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await As(RoleCodes.Customer).GetAsync("/api/customers/me/after-sales/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(RoleCodes.Sales).GetAsync("/api/sales/after-sales/tasks/open")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(RoleCodes.Manager).GetAsync("/api/manager/after-sales/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(RoleCodes.Admin).GetAsync("/api/manager/after-sales/refunds")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(RoleCodes.Admin).GetAsync("/api/admin/after-sales/dashboard")).StatusCode);
        var csv = await As(RoleCodes.Admin).GetAsync("/api/admin/after-sales/dashboard/export?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("metric,description,current (2026-10-01..2026-10-31)", await csv.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AfterSalesErrorCodes.RequestNotFound, 404)]
    [InlineData(AfterSalesErrorCodes.ValidationFailed, 400)]
    [InlineData(AfterSalesErrorCodes.ReasonRequired, 400)]
    [InlineData(SmartSolar.Modules.CustomerContracts.ContractErrorCodes.WarrantyRequired, 400)]
    [InlineData(CommonErrorCodes.Forbidden, 403)]   // e.g. issuing a quotation without owning the Sales task
    [InlineData(AfterSalesErrorCodes.StaleRevision, 409)]
    [InlineData(AfterSalesErrorCodes.PaymentRequired, 409)]
    [InlineData(AfterSalesErrorCodes.AlreadyClaimed, 409)]
    public void After_sales_error_codes_map_to_http_statuses(string code, int status)
        => Assert.Equal(status, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(code));

    [Fact]
    public async Task After_sales_endpoints_follow_the_installation_workflow_feature_gate()
    {
        using var factory = new AuthApiFactory();
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync("/api/customers/me/after-sales/requests"),
            HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
    }
}
