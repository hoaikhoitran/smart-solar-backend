using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// The 2026-11-17 simplified quotation / contract / payment flow through the real API pipeline (authentication, role policies, validation,
/// envelope, SQLite): the negative role matrix of all five roles on every new or re-assigned endpoint, request validation, and the complete
/// journey from the technician's cart to the installation project, once paid in CASH and once ONLINE through a synthetic PayOS server.
/// </summary>
public sealed class SimplifiedCommercialFlowEndpointsTests
{
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static readonly Dictionary<string, string?> GenerousLimits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["RateLimiting:ContractWrite:PermitLimit"] = "1000", ["RateLimiting:ContractRead:PermitLimit"] = "1000",
        ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000",
        ["RateLimiting:InstallationWrite:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false",
        ["Installation:ScheduleWorkerEnabled"] = "false",
    };

    private sealed record Call(string Method, string Path, object? Body = null);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Call call) => call.Method switch
    {
        "GET" => client.GetAsync(call.Path),
        "DELETE" => client.DeleteAsync(call.Path),
        "PUT" => client.PutAsJsonAsync(call.Path, call.Body ?? new { }),
        _ => client.PostAsJsonAsync(call.Path, call.Body ?? new { }),
    };

    /// <summary>Every role outside <paramref name="allowed"/> gets 403, anonymous 401; allowed roles reach the handler (anything but 401/403).</summary>
    private static async Task AssertRolesAsync(AuthApiFactory factory, Call call, params string[] allowed)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(factory.CreateClient(), call)).StatusCode);
        foreach (var role in AllRoles)
        {
            var status = (await SendAsync(CatalogEndpointsTests.ClientFor(factory, role), call)).StatusCode;
            if (allowed.Contains(role)) Assert.True(status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{role} {call.Method} {call.Path}: {status}");
            else Assert.True(status == HttpStatusCode.Forbidden, $"{role} {call.Method} {call.Path}: expected 403, got {(int)status}");
        }
    }

    [Fact]
    public async Task Every_new_or_reassigned_endpoint_refuses_the_other_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var line = new { revision = 1, productId = id, quantity = 1m };

        // Technician cart.
        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/technician/survey-requests/{id}/quotation"), new Call("GET", $"/api/technician/survey-requests/{id}/quotations"),
                     new Call("GET", $"/api/technician/quotations/{id}"), new Call("POST", $"/api/technician/quotations/{id}/lines", line),
                     new Call("PUT", $"/api/technician/quotations/{id}/lines/{id}", new { revision = 1, quantity = 2m }),
                     new Call("DELETE", $"/api/technician/quotations/{id}/lines/{id}?revision=1"), new Call("POST", $"/api/technician/quotations/{id}/issue", new { revision = 1 }),
                     new Call("POST", $"/api/technician/quotations/{id}/revisions"),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Technician);

        // Customer decision.
        foreach (var call in new[] { new Call("POST", $"/api/customers/me/quotations/{id}/accept"), new Call("POST", $"/api/customers/me/quotations/{id}/reject", new { reason = "x" }),
                     new Call("POST", $"/api/customers/me/quotations/{id}/revision-request", new { reason = "x" }) })
            await AssertRolesAsync(factory, call, RoleCodes.Customer);

        // Contract steps: Manager/Admin only (Sales is consult-only); reading stays open to the Sales owner.
        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/quotations/{id}/contracts", new { terms = "t" }), new Call("PUT", $"/api/contracts/{id}", new { revision = 0, terms = "t" }),
                     new Call("POST", $"/api/contracts/{id}/issue", new { revision = 0 }), new Call("POST", $"/api/contracts/{id}/discard", new { revision = 0, reason = "x" }),
                     new Call("PUT", $"/api/contracts/{id}/official-document", new { revision = 0, fileId = id, canonicalHash = "x" }),
                     new Call("POST", $"/api/contracts/{id}/publish", new { revision = 0 }), new Call("POST", $"/api/contracts/{id}/review", new { revision = 0, decision = "APPROVE" }),
                     new Call("POST", $"/api/contracts/{id}/amendments", new { terms = "t" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, new Call("GET", $"/api/contracts/{id}"), RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin);

        // Payments: the customer pays or chooses cash; only Manager/Admin read payment history and confirm cash.
        await AssertRolesAsync(factory, new Call("POST", $"/api/customers/me/contracts/{id}/payments/cash"), RoleCodes.Customer);
        await AssertRolesAsync(factory, new Call("POST", $"/api/customers/me/contracts/{id}/payments"), RoleCodes.Customer);
        await AssertRolesAsync(factory, new Call("GET", $"/api/contracts/{id}/payments"), RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, new Call("POST", $"/api/contracts/{id}/payments/cash/confirm", new { amount = 1m, receiptReference = "R", evidenceFileId = id }),
            RoleCodes.Manager, RoleCodes.Admin);

        // Installation project: Manager/Admin only.
        await AssertRolesAsync(factory, new Call("POST", $"/api/contracts/{id}/installation-project"), RoleCodes.Manager, RoleCodes.Admin);
    }

    [Fact]
    public async Task Requests_are_validated_before_any_work()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);

        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/lines", new { revision = 1, quantity = 1m }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/lines", new { productId = id, quantity = 1m }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/lines", new { revision = 1, productId = id, quantity = 0m }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PutAsJsonAsync($"/api/technician/quotations/{id}/lines/{id}", new { quantity = 1m }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.DeleteAsync($"/api/technician/quotations/{id}/lines/{id}"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        // Unknown resources: 404, not someone else's data.
        await AssertFailureAsync(await technician.PostAsync($"/api/technician/survey-requests/{id}/quotation", null), HttpStatusCode.NotFound, QuotationErrorCodes.NotAssignedTechnician);
        await AssertFailureAsync(await technician.GetAsync($"/api/technician/quotations/{id}"), HttpStatusCode.NotFound, QuotationErrorCodes.NotFound);

        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{id}/payments/cash/confirm", new { receiptReference = "R", evidenceFileId = id }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{id}/payments/cash/confirm", new { amount = 1m, receiptReference = "R" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{id}/payments/cash/confirm", new { amount = 1m, receiptReference = " ", evidenceFileId = id }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{id}/payments/cash/confirm", new { amount = 1m, receiptReference = "R", evidenceFileId = id }),
            HttpStatusCode.NotFound, PaymentErrorCodes.ContractNotFound);
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer).PostAsync($"/api/customers/me/contracts/{id}/payments/cash", null),
            HttpStatusCode.NotFound, PaymentErrorCodes.ContractNotFound);
    }

    // ------------------------------------------------------------------ full journeys

    private sealed record World(AuthApiFactory Factory, FakeStorageObjectVerifier Storage, FakePayOsServer PayOs, InstallationWorkflowTestContext Ctx,
        QuotationCatalog Catalog, ConfirmedSurvey Survey) : IAsyncDisposable
    {
        public HttpClient Technician => CatalogEndpointsTests.ClientFor(Factory, RoleCodes.Technician, Survey.Technician.Id);
        public HttpClient Customer => CatalogEndpointsTests.ClientFor(Factory, RoleCodes.Customer, Survey.Request.Customer.Id);

        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            Factory.Dispose();
        }
    }

    private static async Task<World> WorldAsync()
    {
        var storage = new FakeStorageObjectVerifier();
        var payOs = new FakePayOsServer();
        var factory = Enabled(storage, new FakeRagServiceClient(), GenerousLimits, services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(payOs.Gateway());
        });
        _ = factory.Services;   // build the host (schema + seeded roles) before seeding the journey
        var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        var survey = await ctx.QuotableSurveyAsync();
        await ctx.EnsureInstallationWarrantyOptionAsync();
        return new World(factory, storage, payOs, ctx, catalog, survey);
    }

    /// <summary>Technician cart → issue → customer accepts → Manager prepares, publishes and confirms the signature; returns the SIGNED contract id.</summary>
    private static async Task<(Guid ContractId, decimal Total, HttpClient Manager, Guid ManagerId)> SignedThroughTheApiAsync(World w)
    {
        var technician = w.Technician;
        var requestId = w.Survey.Request.Request.Id;
        var cart = await DataOf(await technician.PostAsJsonAsync($"/api/technician/survey-requests/{requestId}/quotation", new { notes = "South roof" }), HttpStatusCode.Created);
        var quotationId = cart.GetProperty("quotationId").GetGuid();
        var revision = cart.GetProperty("revision").GetInt32();
        foreach (var (product, quantity) in new[] { (w.Catalog.Panel.Id, 2m), (w.Catalog.Inverter.Id, 1m), (w.Catalog.Labor.Id, 1m) })
        {
            // A price field in the body is ignored: the price always comes from the catalog.
            var added = await DataOf(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/lines",
                new { revision, productId = product, quantity, unitPrice = 1m }));
            revision = added.GetProperty("revision").GetInt32();
        }
        var issued = await DataOf(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/issue", new { revision }));
        Assert.Equal(QuotationStatuses.Issued, issued.GetProperty("status").GetString());
        var total = issued.GetProperty("totalAmount").GetDecimal();
        Assert.Equal(Math.Round(18_000_000m * 1.08m), total);   // 2 × 3,000,000 + 10,000,000 + 2,000,000, default tax 8 %
        // The technician can no longer change it.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/lines", new { revision = issued.GetProperty("revision").GetInt32(),
            productId = w.Catalog.Transport.Id, quantity = 1m }), HttpStatusCode.Conflict, QuotationErrorCodes.NotDraft);

        var customer = w.Customer;
        var mine = await DataOf(await customer.GetAsync("/api/customers/me/quotations"));
        Assert.Equal(quotationId, Assert.Single(mine.EnumerateArray()).GetProperty("quotationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, mine[0].GetProperty("lines")[0].GetProperty("catalogUnitPrice").ValueKind);
        await DataOf(await customer.PostAsync($"/api/customers/me/quotations/{quotationId}/accept", null));
        Assert.Empty((await DataOf(await customer.GetAsync("/api/customers/me/contracts"))).EnumerateArray());   // the draft is not the customer's yet

        var managerId = Guid.NewGuid();
        var manager = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Manager, managerId);
        var contracts = await DataOf(await manager.GetAsync($"/api/survey-requests/{requestId}/contracts"));
        var draft = Assert.Single(contracts.EnumerateArray());
        Assert.Equal(ContractStatuses.Draft, draft.GetProperty("status").GetString());
        Assert.Equal(total, draft.GetProperty("totalAmount").GetDecimal());
        var contractId = draft.GetProperty("contractId").GetGuid();
        // Sales (consult-only) cannot touch it.
        Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Sales, w.Survey.Sales.Id)
            .PostAsJsonAsync($"/api/contracts/{contractId}/issue", new { revision = draft.GetProperty("revision").GetInt32() })).StatusCode);

        var terms = await DataOf(await manager.PutAsJsonAsync($"/api/contracts/{contractId}", new { revision = draft.GetProperty("revision").GetInt32(), terms = "Standard terms" }));
        var contractIssued = await DataOf(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/issue", new { revision = terms.GetProperty("revision").GetInt32() }));
        var pdf = await UploadAsync(manager, w.Storage, managerId, FilePurposes.ContractDocument);
        var attached = await DataOf(await manager.PutAsJsonAsync($"/api/contracts/{contractId}/official-document", new
        {
            revision = contractIssued.GetProperty("revision").GetInt32(), fileId = pdf, canonicalHash = contractIssued.GetProperty("canonicalHash").GetString(),
        }));
        var published = await DataOf(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/publish", new { revision = attached.GetProperty("revision").GetInt32() }));
        Assert.Equal(ContractStatuses.AwaitingSignature, published.GetProperty("status").GetString());

        // Payment is closed until the signature is confirmed.
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null), HttpStatusCode.Conflict, PaymentErrorCodes.ContractNotSigned);
        var scan = await UploadAsync(customer, w.Storage, w.Survey.Request.Customer.Id, FilePurposes.SignedContract);
        var under = await DataOf(await customer.PostAsJsonAsync($"/api/customers/me/contracts/{contractId}/signed-document", new
        {
            fileId = scan, officialDocumentSha256 = published.GetProperty("officialDocument").GetProperty("sha256").GetString(),
        }));
        var signed = await DataOf(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/review", new { revision = under.GetProperty("revision").GetInt32(), decision = "APPROVE" }));
        Assert.Equal(ContractStatuses.Signed, signed.GetProperty("status").GetString());
        return (contractId, total, manager, managerId);
    }

    [Fact]
    public async Task Cash_journey_from_the_technician_cart_to_the_installation_project()
    {
        await using var w = await WorldAsync();
        var (contractId, total, manager, managerId) = await SignedThroughTheApiAsync(w);
        var customer = w.Customer;

        // No project before PAID.
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ContractNotPaid);

        var cash = await DataOf(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null));
        Assert.Equal("CASH", cash.GetProperty("method").GetString());
        var state = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/payments"));
        Assert.Equal(ContractPaymentStates.AwaitingCashConfirmation, state.GetProperty("paymentState").GetString());
        Assert.Equal(ContractStatuses.Signed, state.GetProperty("contractStatus").GetString());
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments", null), HttpStatusCode.Conflict, PaymentErrorCodes.CashSelected);
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ContractNotPaid);

        // The customer cannot confirm cash; a wrong amount is refused; the exact amount with receipt and evidence pays the contract once.
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"/api/contracts/{contractId}/payments/cash/confirm",
            new { amount = total, receiptReference = "PT-1", evidenceFileId = Guid.NewGuid() })).StatusCode);
        var evidence = await UploadAsync(manager, w.Storage, managerId, FilePurposes.PaymentEvidence);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/payments/cash/confirm",
            new { amount = total - 1_000m, receiptReference = "PT-1", evidenceFileId = evidence }), HttpStatusCode.Conflict, PaymentErrorCodes.CashAmountMismatch);
        var paid = await DataOf(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/payments/cash/confirm",
            new { amount = total, receiptReference = "PT-1", evidenceFileId = evidence, note = "Office cashier" }));
        Assert.Equal(PaymentStatuses.Paid, paid.GetProperty("status").GetString());
        Assert.Equal(managerId, paid.GetProperty("confirmedByUserId").GetGuid());
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/payments/cash/confirm",
            new { amount = total, receiptReference = "PT-2", evidenceFileId = evidence }), HttpStatusCode.Conflict, PaymentErrorCodes.AlreadyPaid);

        var history = await DataOf(await manager.GetAsync($"/api/contracts/{contractId}/payments"));
        Assert.Equal(ContractPaymentStates.Paid, history.GetProperty("paymentState").GetString());
        Assert.Equal(ContractStatuses.Paid, (await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}"))).GetProperty("status").GetString());

        var project = await DataOf(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Created);
        Assert.Equal(contractId, project.GetProperty("contractId").GetGuid());
    }

    /// <summary>
    /// 2026-11-17 schedule journey: after SIGNED the surveying technician records the agreed date through the API, the customer's mobile API
    /// shows it as PLANNED (not confirmed), the cash confirmation makes the system create the project and confirm the schedule, and the
    /// customer then sees CONFIRMED with the project; no Manager step is needed and a second project is refused.
    /// </summary>
    [Fact]
    public async Task Technician_agreed_schedule_is_planned_after_signature_and_confirmed_by_the_cash_payment_through_the_api()
    {
        await using var w = await WorldAsync();
        var (contractId, total, manager, managerId) = await SignedThroughTheApiAsync(w);
        var customer = w.Customer;
        // A Tuesday 1–2 weeks ahead in the scheduling time zone (inside the seeded technician's working week and the planning horizon).
        var local = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date.AddDays(8);
        while (local.DayOfWeek != DayOfWeek.Tuesday) local = local.AddDays(1);
        var starts = new DateTimeOffset(local.AddHours(8), TimeSpan.FromHours(7));
        var plan = new { startsAt = starts, endsAt = starts.AddHours(4), customerAgreementNote = "Khách đồng ý qua điện thoại" };

        // Another technician cannot see or plan it.
        var stranger = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Technician, Guid.NewGuid());
        await AssertFailureAsync(await stranger.PostAsJsonAsync($"/api/technicians/me/contracts/{contractId}/installation-schedule", plan),
            HttpStatusCode.NotFound, InstallationScheduleErrorCodes.ContractNotFound);

        var planned = await DataOf(await w.Technician.PostAsJsonAsync($"/api/technicians/me/contracts/{contractId}/installation-schedule", plan));
        Assert.Equal((InstallationScheduleStatuses.Planned, false, true),
            (planned.GetProperty("status").GetString(), planned.GetProperty("isConfirmed").GetBoolean(), planned.GetProperty("awaitingPayment").GetBoolean()));
        var mobile = await DataOf(await customer.GetAsync("/api/customers/me/installation-schedules"));
        Assert.Equal((InstallationScheduleStatuses.Planned, false), (mobile[0].GetProperty("status").GetString(), mobile[0].GetProperty("isConfirmed").GetBoolean()));
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ContractNotPaid);

        await DataOf(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null));
        var evidence = await UploadAsync(manager, w.Storage, managerId, FilePurposes.PaymentEvidence);
        await DataOf(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/payments/cash/confirm", new { amount = total, receiptReference = "PT-S1", evidenceFileId = evidence }));

        var confirmed = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/installation-schedule"));
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (confirmed[0].GetProperty("status").GetString(), confirmed[0].GetProperty("isConfirmed").GetBoolean()));
        var projectId = confirmed[0].GetProperty("projectId").GetGuid();
        var project = await DataOf(await customer.GetAsync($"/api/customers/me/installation-projects/{projectId}"));
        Assert.Equal(InstallationStatuses.Scheduled, project.GetProperty("status").GetString());
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ProjectExists);
        var staff = await DataOf(await manager.GetAsync("/api/manager/installation-schedules?status=CONFIRMED"));
        Assert.Equal(contractId, Assert.Single(staff.EnumerateArray()).GetProperty("contractId").GetGuid());
    }

    [Fact]
    public async Task Payos_journey_from_the_technician_cart_to_the_installation_project_with_a_duplicate_webhook()
    {
        await using var w = await WorldAsync();
        var (contractId, total, manager, _) = await SignedThroughTheApiAsync(w);
        var customer = w.Customer;

        var link = await DataOf(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments", null));
        Assert.Equal("ONLINE", link.GetProperty("method").GetString());
        Assert.False(string.IsNullOrEmpty(link.GetProperty("checkoutUrl").GetString()));
        // Cash cannot be chosen while the link is open.
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null), HttpStatusCode.Conflict, PaymentErrorCodes.CheckoutInProgress);
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ContractNotPaid);

        var orderCode = link.GetProperty("orderCode").GetInt64();
        var body = w.PayOs.Webhook(orderCode, (long)total, "FT-E2E");
        var anonymous = w.Factory.CreateClient();
        var first = await DataOf(await anonymous.PostAsync("/api/payments/payos/webhook", new StringContent(body, System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(WebhookOutcomes.Applied, first.GetProperty("outcome").GetString());
        var repeat = await DataOf(await anonymous.PostAsync("/api/payments/payos/webhook", new StringContent(body, System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(WebhookOutcomes.Duplicate, repeat.GetProperty("outcome").GetString());

        var state = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/payments"));
        Assert.Equal(ContractPaymentStates.Paid, state.GetProperty("paymentState").GetString());
        await using (var db = w.Factory.CreateDbContext())
            Assert.Equal(1, await db.PaymentAttempts.CountAsync(a => a.ContractId == contractId && a.Status == PaymentStatuses.Paid));
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null), HttpStatusCode.Conflict, PaymentErrorCodes.AlreadyPaid);

        var project = await DataOf(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Created);
        Assert.Equal(contractId, project.GetProperty("contractId").GetGuid());
    }
}
