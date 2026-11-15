using System.Net;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// DS-02b: the contract document URL endpoints the frontend already calls (<c>…/official-document/url</c>, <c>…/submissions/{id}/url</c>) exist for
/// staff and for the customer, need a token and the right role, answer 404 for unknown or foreign contracts, 400 for an unknown disposition and
/// never let a cache keep the (bearer) URL. The document rules themselves are unit-tested in ContractDocumentUrlTests.
/// </summary>
public sealed class ContractDocumentEndpointsTests
{
    private static string Staff(Guid id) => $"/api/contracts/{id}/official-document/url";
    private static string StaffSubmission(Guid id, Guid submission) => $"/api/contracts/{id}/submissions/{submission}/url";
    private static string Customer(Guid id) => $"/api/customers/me/contracts/{id}/official-document/url";
    private static string CustomerSubmission(Guid id, Guid submission) => $"/api/customers/me/contracts/{id}/submissions/{submission}/url";

    [Fact]
    public async Task Document_url_endpoints_exist_for_staff_and_customers_with_the_contract_role_matrix()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var submission = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Staff(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Customer(id))).StatusCode);
        // Technicians never read contracts; customers do not use the staff route and staff do not use the customer route.
        Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician).GetAsync(Staff(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync(Staff(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales).GetAsync(Customer(id))).StatusCode);

        foreach (var role in new[] { RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            var missing = await client.GetAsync(Staff(id));
            await AssertFailureAsync(missing, HttpStatusCode.NotFound, ContractErrorCodes.NotFound);
            Assert.True(missing.Headers.CacheControl?.NoStore, "URL responses must never be cached");
            await AssertFailureAsync(await client.GetAsync(StaffSubmission(id, submission)), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);
            await AssertFailureAsync(await client.GetAsync(Staff(id) + "?disposition=download"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        await AssertFailureAsync(await customer.GetAsync(Customer(id)), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);
        await AssertFailureAsync(await customer.GetAsync(CustomerSubmission(id, submission)), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);
        await AssertFailureAsync(await customer.GetAsync(Customer(id) + "?disposition=print"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task The_document_url_endpoints_follow_the_installation_workflow_feature_gate()
    {
        using var factory = new AuthApiFactory();
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync(Customer(Guid.NewGuid())),
            HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
    }
}
