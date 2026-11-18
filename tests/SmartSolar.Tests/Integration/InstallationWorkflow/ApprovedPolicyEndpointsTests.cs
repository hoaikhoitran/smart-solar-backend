using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Endpoints of the approved policies through the real pipeline: only a Manager decides equipment substitutions (the role matrix names no
/// other approver, so Admin is refused); the retired pricing-approval routes answer 410; only the project's customer agrees to a substitution. Unknown or foreign resources answer 404, invalid bodies 400.
/// </summary>
public sealed class ApprovedPolicyEndpointsTests
{
    private static string Decide(Guid id) => $"/api/manager/quotations/{id}/pricing-approval";
    private static string Request(Guid id) => $"/api/quotations/{id}/pricing-approval-request";
    private static string ManagerSubstitution(Guid id) => $"/api/installation-projects/{id}/equipment-substitution/approve";
    private static string CustomerSubstitution(Guid id) => $"/api/customers/me/installation-projects/{id}/equipment-substitution/approve";

    /// <summary>2026-11-17: pricing exceptions are retired (catalog prices only); the routes stay to answer 410 ENDPOINT_RETIRED, behind the same roles.</summary>
    [Fact]
    public async Task Pricing_approval_endpoints_are_retired_and_still_behind_their_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var decision = new { revision = 1, approve = true, reason = "ok" };
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Decide(id), decision)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/manager/quotations/pricing-approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Request(id), new { revision = 1, reason = "x" })).StatusCode);
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Decide(id), decision)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/manager/quotations/pricing-approvals")).StatusCode);
        }
        foreach (var role in new[] { RoleCodes.Technician, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(Request(id), new { revision = 1, reason = "x" })).StatusCode);

        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        await AssertFailureAsync(await manager.GetAsync("/api/manager/quotations/pricing-approvals"), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        await AssertFailureAsync(await manager.PostAsJsonAsync(Decide(id), decision), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        foreach (var role in new[] { RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin })
            await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(Request(id), new { revision = 1, reason = "promo" }),
                HttpStatusCode.Gone, "ENDPOINT_RETIRED");
    }

    [Fact]
    public async Task Only_a_manager_approves_a_substitution_and_the_customer_step_is_retired()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var body = new { expectedRevision = 1, reason = "Out of stock" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(ManagerSubstitution(id), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync(CustomerSubstitution(id), null)).StatusCode);
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(ManagerSubstitution(id), body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsync(CustomerSubstitution(id), null)).StatusCode);

        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        await AssertFailureAsync(await manager.PostAsJsonAsync(ManagerSubstitution(id), new { reason = "x" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync(ManagerSubstitution(id), body), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        // The customer's substitution agreement was retired (2026-11-18): the Manager's approval is internal and final.
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).PostAsync(CustomerSubstitution(id), null),
            HttpStatusCode.Gone, "ENDPOINT_RETIRED");
    }

    [Fact]
    public async Task Terminal_outcomes_and_audience_changes_are_limited_to_their_actor()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        string Reject(Guid q) => $"/api/customers/me/quotations/{q}/reject";
        string Withdraw(Guid r) => $"/api/customers/me/survey-requests/{r}/withdraw";
        string Close(Guid r) => $"/api/survey-requests/{r}/close";
        string Audience(Guid d) => $"/api/admin/knowledge-documents/{d}/audience";
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Reject(id), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Withdraw(id), new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Close(id), new { reasonCode = "NO_RESPONSE" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Audience(id), new { revision = 0, audience = "CUSTOMER", reason = "x" })).StatusCode);
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Reject(id), new { reason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Withdraw(id), new { reason = "x" })).StatusCode);
        }
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Technician, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(Close(id), new { reasonCode = "NO_RESPONSE" })).StatusCode);
        foreach (var role in new[] { RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await CatalogEndpointsTests.ClientFor(factory, role).PutAsJsonAsync(Audience(id), new { revision = 0, audience = "CUSTOMER", reason = "x" })).StatusCode);

        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        // The customer's quotation decision was retired (2026-11-18): the technician records it on site.
        await AssertFailureAsync(await customer.PostAsJsonAsync(Reject(id), new { reason = "x" }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        await AssertFailureAsync(await customer.PostAsJsonAsync(Withdraw(id), new { reason = "x" }), HttpStatusCode.NotFound, SalesOutcomeErrorCodes.RequestNotFound);
        var sales = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales);
        await AssertFailureAsync(await sales.PostAsJsonAsync(Close(id), new { reasonCode = "BORED" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await sales.PostAsJsonAsync(Close(id), new { reasonCode = "NO_RESPONSE" }), HttpStatusCode.NotFound, SalesOutcomeErrorCodes.RequestNotFound);
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        await AssertFailureAsync(await admin.PutAsJsonAsync(Audience(id), new { revision = 0, audience = "PUBLIC", reason = "x" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await admin.PutAsJsonAsync(Audience(id), new { revision = 0, audience = "CUSTOMER", reason = "x" }), HttpStatusCode.NotFound,
            SmartSolar.Modules.KnowledgeBase.KnowledgeErrorCodes.DocumentNotFound);
    }

    [Fact]
    public async Task Only_after_sales_staff_record_a_verified_warranty_activation()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var path = $"/api/manager/after-sales/assets/{Guid.NewGuid()}/warranty-activation";
        var body = new { activatedAt = "2026-10-01T09:00:00+07:00", evidenceFileId = Guid.NewGuid(), reason = "certificate" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(path, body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(path, body)).StatusCode);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        await AssertFailureAsync(await manager.PostAsJsonAsync(path, new { activatedAt = "yesterday", evidenceFileId = Guid.NewGuid(), reason = "x" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync(path, new { activatedAt = "2026-10-01T09:00:00+07:00", evidenceFileId = Guid.NewGuid(), reason = " " }),
            HttpStatusCode.BadRequest, "AFTER_SALES_REASON_REQUIRED");
    }

    /// <summary>2026-11-17: Sales is consult-only; Manager/Admin own every contract step, discard included.</summary>
    [Fact]
    public async Task Only_a_manager_or_admin_discards_a_draft_contract()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var path = $"/api/contracts/{Guid.NewGuid()}/discard";
        var body = new { revision = 0, reason = "Customer withdrew" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(path, body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(path, body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Manager, RoleCodes.Admin })
        {
            var staff = CatalogEndpointsTests.ClientFor(factory, role);
            await AssertFailureAsync(await staff.PostAsJsonAsync(path, new { reason = "x" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            await AssertFailureAsync(await staff.PostAsJsonAsync(path, new { revision = 0, reason = " " }), HttpStatusCode.BadRequest,
                SmartSolar.Modules.CustomerContracts.ContractErrorCodes.DiscardReasonRequired);
            await AssertFailureAsync(await staff.PostAsJsonAsync(path, body), HttpStatusCode.NotFound, SmartSolar.Modules.CustomerContracts.ContractErrorCodes.NotFound);
        }
    }
}

