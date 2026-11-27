using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// FE feedback after the Flow 1–4 audit through the real API (2026-11-25): ISSUE-01 the contract id after AGREED opens the contract with its 30/70
/// plan (bundled per-panel packages), ISSUE-02 <c>customerName</c> on <c>GET /api/installation-projects</c> (array and paged), RBAC unchanged. Data is
/// built with the production handlers on the API's database.
/// </summary>
public sealed class FeFeedbackFixesEndpointsTests
{
    private static readonly Dictionary<string, string?> Limits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["RateLimiting:ContractRead:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["RateLimiting:ContractWrite:PermitLimit"] = "1000", ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    [Fact]
    public async Task ISSUE01_the_contract_id_after_agreed_opens_the_contract_and_its_30_70_plan_through_the_api()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), Limits);
        _ = factory.Services;
        await using var after = AfterSalesTestContext.CreateOn(factory.CreateDbContext());
        var (_, flow) = await PerPanelServicePackageTests.AgreedBundleAsync(on: after);
        var quotationId = flow.Cart.Quotation.QuotationId;

        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, flow.TechnicianId);
        var detail = await DataOf(await technician.GetAsync($"/api/technician/quotations/{quotationId}"));
        Assert.Equal(flow.ContractId, detail.GetProperty("contractId").GetGuid());
        var list = await DataOf(await technician.GetAsync($"/api/technician/survey-requests/{flow.Cart.RequestId}/quotations"));
        Assert.Equal(flow.ContractId, list.EnumerateArray().Single(q => q.GetProperty("quotationId").GetGuid() == quotationId).GetProperty("contractId").GetGuid());

        // The technician opens the contract page with that id: contract + installment plan (30 % then 70 %) + bundled packages.
        var contract = await DataOf(await technician.GetAsync($"/api/contracts/{flow.ContractId}"));
        Assert.Equal(flow.ContractId, contract.GetProperty("contractId").GetGuid());
        Assert.Equal(quotationId, contract.GetProperty("quotationId").GetGuid());
        var total = contract.GetProperty("totalAmount").GetDecimal();
        var installments = contract.GetProperty("paymentPlan").GetProperty("installments").EnumerateArray().OrderBy(i => i.GetProperty("sequence").GetInt32()).ToList();
        Assert.Equal([30m, 70m], installments.Select(i => i.GetProperty("percent").GetDecimal()));
        Assert.Equal(total, installments.Sum(i => i.GetProperty("amount").GetDecimal()));
        Assert.All(installments, i => Assert.NotEqual("PAID", i.GetProperty("status").GetString()));
        Assert.NotEqual(0, contract.GetProperty("servicePackages").GetArrayLength());

        // The customer reads the same contract; a Manager too; anonymous and other customers do not.
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, flow.CustomerId);
        var mine = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{flow.ContractId}"));
        Assert.Equal(2, mine.GetProperty("paymentPlan").GetProperty("installments").GetArrayLength());
        await DataOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager).GetAsync($"/api/contracts/{flow.ContractId}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/contracts/{flow.ContractId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).GetAsync($"/api/customers/me/contracts/{flow.ContractId}")).StatusCode);
    }

    [Fact]
    public async Task ISSUE02_manager_project_list_carries_customer_name_in_array_and_paged_responses()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), Limits);
        _ = factory.Services;
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);

        // Empty: array [] and an empty page.
        Assert.Equal(0, (await DataOf(await manager.GetAsync("/api/installation-projects"))).GetArrayLength());
        var empty = await DataOf(await manager.GetAsync("/api/installation-projects?page=1&pageSize=10"));
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("totalItems").GetInt32());

        await using var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var t = await FeFeedbackFixesTests.TwoProjectsAsync(ctx);
        var expected = new Dictionary<Guid, string> { [t.First.ProjectId] = "Nguyễn Văn An", [t.Second.ProjectId] = "Trần Thị Bình" };

        var array = await DataOf(await manager.GetAsync("/api/installation-projects"));
        Assert.Equal(2, array.GetArrayLength());
        foreach (var row in array.EnumerateArray())
        {
            Assert.Equal(expected[row.GetProperty("projectId").GetGuid()], row.GetProperty("customerName").GetString());
            // Every previous field is still there.
            foreach (var field in new[] { "projectId", "contractId", "status", "responsibleTechnicianUserId", "responsibleTechnicianName", "technicianAccepted",
                         "scheduledStartsAt", "scheduledEndsAt", "operationalZoneId", "createdAt", "revision" })
                Assert.True(row.TryGetProperty(field, out _), field);
            // Only the display name: no customer contact data on the list row.
            Assert.False(row.TryGetProperty("customerPhone", out _));
            Assert.False(row.TryGetProperty("customerEmail", out _));
        }

        var seen = new HashSet<Guid>();
        for (var page = 1; page <= 2; page++)
        {
            var paged = await DataOf(await manager.GetAsync($"/api/installation-projects?page={page}&pageSize=1"));
            Assert.Equal(2, paged.GetProperty("totalItems").GetInt32());
            var item = Assert.Single(paged.GetProperty("items").EnumerateArray());
            var id = item.GetProperty("projectId").GetGuid();
            Assert.True(seen.Add(id));
            Assert.Equal(expected[id], item.GetProperty("customerName").GetString());
        }

        // Admin keeps access; other roles and anonymous do not; envelope unchanged.
        var admin = await BodyOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin).GetAsync("/api/installation-projects?status=" + t.First.Status));
        Assert.True(admin.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrEmpty(admin.GetProperty("traceId").GetString()));
        Assert.Equal(2, admin.GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/installation-projects")).StatusCode);
        foreach (var role in new[] { RoleCodes.Technician, RoleCodes.Customer, RoleCodes.Sales })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).GetAsync("/api/installation-projects")).StatusCode);
        // Invalid paging still answers 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/installation-projects?page=0&pageSize=10")).StatusCode);
        Assert.Equal(JsonValueKind.Array, (await DataOf(await manager.GetAsync("/api/installation-projects?status=COMPLETED"))).ValueKind);
    }

    [Fact]
    public async Task ISSUE03_removing_a_cash_choice_reopens_the_deposit_unpaid_and_the_paper_signature_waits_for_the_verified_deposit()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), Limits);
        _ = factory.Services;
        await using var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx, "Cash customer");
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, flow.TechnicianId);
        var route = $"/api/technician/contracts/{flow.ContractId}";

        // Cash chosen: not paid, waiting for a Manager/Admin receipt.
        var cash = await DataOf(await technician.PostAsJsonAsync($"{route}/installments/1/payment", new { method = "CASH" }));
        Assert.Equal("AWAITING_CASH_CONFIRMATION", cash.GetProperty("paymentState").GetString());
        Assert.Equal("DUE", cash.GetProperty("installmentStatus").GetString());
        // The paper signature cannot start yet.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{route}/signature-set", new { expectedPageCount = 2 }), HttpStatusCode.Conflict, "CONTRACT_DEPOSIT_REQUIRED");

        // Removing the cash choice: the deposit is payable again (cash or PayOS), still unpaid; nothing was recorded as received.
        var removed = await DataOf(await technician.PostAsync($"{route}/installments/1/payment/cancel", null));
        Assert.Equal("AWAITING_PAYMENT", removed.GetProperty("paymentState").GetString());
        Assert.Equal("DUE", removed.GetProperty("installmentStatus").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("paidAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("current").ValueKind);
        Assert.Equal("CANCELLED", Assert.Single(removed.GetProperty("attempts").EnumerateArray()).GetProperty("status").GetString());
        await AssertFailureAsync(await technician.PostAsync($"{route}/installments/1/payment/cancel", null), HttpStatusCode.NotFound, "PAYMENT_ATTEMPT_NOT_FOUND");

        // Cash again, confirmed by a Manager with a receipt: PAID with the receipt reference / time / amount for the FE receipt; no longer cancellable.
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        var paid = await DataOf(await technician.GetAsync($"{route}/installments/1/payment"));
        Assert.Equal("PAID", paid.GetProperty("paymentState").GetString());
        var settled = paid.GetProperty("attempts").EnumerateArray().Single(a => a.GetProperty("status").GetString() == "PAID");
        Assert.Equal("CASH", settled.GetProperty("method").GetString());
        Assert.StartsWith("PT-", settled.GetProperty("providerReference").GetString());
        Assert.Equal(paid.GetProperty("amount").GetDecimal(), settled.GetProperty("amountPaid").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, settled.GetProperty("paidAt").ValueKind);
        Assert.False((await technician.PostAsync($"{route}/installments/1/payment/cancel", null)).IsSuccessStatusCode);
        Assert.Equal("PAID", (await DataOf(await technician.GetAsync($"{route}/installments/1/payment"))).GetProperty("paymentState").GetString());
        // The balance stays closed until the customer accepts the installation.
        Assert.Equal("NOT_PAYABLE", (await DataOf(await technician.GetAsync($"{route}/installments/2/payment"))).GetProperty("paymentState").GetString());
        // Now the paper signature can start.
        await DataOf(await technician.PostAsJsonAsync($"{route}/signature-set", new { expectedPageCount = 2 }));
    }
}
