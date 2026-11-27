using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// FE Flow 1–4 fixes through the real API (2026-11-25): equipment quantity validation envelope (BUG-03), quotation contractId on every read (BUG-04),
/// separate rate-limit bucket for lightweight dispatch reads (BUG-05). Data up to the agreement is built with the production handlers on the API's database.
/// </summary>
public sealed class FeFlowFixesEndpointsTests
{
    private static readonly Dictionary<string, string?> Limits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    [Fact]
    public async Task BUG03_BUG04_quantity_validation_and_contract_id_through_the_api()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), Limits);
        _ = factory.Services;
        await using var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());

        // BUG-03: 1.5 panels → 400 VALIDATION_FAILED with a field error on quantity; 2 panels accepted.
        var draft = await SimplifiedFlowFixture.DraftAsync(ctx, "Quantity customer");
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, draft.TechnicianId);
        var route = $"/api/technician/quotations/{draft.Quotation.QuotationId}/lines";
        var refused = await technician.PostAsJsonAsync(route, new { revision = draft.Quotation.Revision, productId = draft.Catalog.Panel.Id, quantity = 1.5m });
        await AssertFailureAsync(refused, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var body = await BodyOf(refused);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.GetProperty("error").GetProperty("details").GetProperty("quantity").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        var accepted = await DataOf(await technician.PostAsJsonAsync(route, new { revision = draft.Quotation.Revision, productId = draft.Catalog.Panel.Id, quantity = 2m }));
        Assert.Equal(2m, accepted.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // BUG-04: after the on-site agreement, detail and list both carry the contract id.
        var flow = await PaymentPlanFlow.AgreedAsync(ctx, "Contract customer");
        var agreedBy = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, flow.TechnicianId);
        var detail = await DataOf(await agreedBy.GetAsync($"/api/technician/quotations/{flow.Cart.Quotation.QuotationId}"));
        Assert.Equal(flow.ContractId, detail.GetProperty("contractId").GetGuid());
        var list = await DataOf(await agreedBy.GetAsync($"/api/technician/survey-requests/{flow.Cart.RequestId}/quotations"));
        Assert.Equal(flow.ContractId, list.EnumerateArray().Single(q => q.GetProperty("quotationId").GetGuid() == flow.Cart.Quotation.QuotationId).GetProperty("contractId").GetGuid());
        // Another technician: 404, nothing leaked.
        await AssertFailureAsync(await technician.GetAsync($"/api/technician/quotations/{flow.Cart.Quotation.QuotationId}"), HttpStatusCode.NotFound, "QUOTATION_NOT_FOUND");
    }

    [Fact]
    public async Task BUG05_lightweight_dispatch_reads_have_their_own_bucket_and_keep_returning_429()
    {
        var limits = new Dictionary<string, string?>(Limits)
        {
            ["RateLimiting:DispatchRead:PermitLimit"] = "3", ["RateLimiting:DispatchRead:WindowMinutes"] = "60",
            ["RateLimiting:SchedulingRead:PermitLimit"] = "3", ["RateLimiting:SchedulingRead:WindowMinutes"] = "60",
        };
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), limits);
        var id = Guid.NewGuid();

        // Customer: polling the dispatch request exhausts only the dispatch-read bucket; slot availability (scheduling-read) still answers.
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await customer.GetAsync($"/api/customers/me/survey-requests/{id}/dispatch-request")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await customer.GetAsync($"/api/customers/me/survey-requests/{id}/dispatch-request")).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await customer.GetAsync($"/api/survey-slots?propertySiteId={id}&date=2026-12-01")).StatusCode);

        // Manager: detail reads exhaust dispatch-read; the expensive candidate computation keeps its own scheduling-read quota (and its own 429).
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await manager.GetAsync($"/api/admin/dispatch-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await manager.GetAsync("/api/admin/dispatch-requests")).StatusCode);
        var candidates = $"/api/admin/dispatch-requests/{id}/candidates?startsAt=2026-12-01T08:00:00%2B07:00";
        for (var i = 0; i < 3; i++) Assert.NotEqual(HttpStatusCode.TooManyRequests, (await manager.GetAsync(candidates)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await manager.GetAsync(candidates)).StatusCode);
        // Authorization unchanged.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/admin/dispatch-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync($"/api/admin/dispatch-requests/{id}")).StatusCode);
    }
}
