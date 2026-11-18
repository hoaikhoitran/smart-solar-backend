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
/// The commercial flow through the real API pipeline (authentication, role policies, validation, envelope, SQLite), 30/70 plan since 2026-11-19:
/// the negative role matrix of all five roles on every new or re-assigned endpoint, request validation, and the journey from the technician's cart
/// through the agreement contract, the deposit (once in CASH, once ONLINE through a synthetic PayOS server) and the paper signature to the project.
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
                     // Technician-led on-site sales (2026-11-18): decision and payment are recorded by the technician only.
                     new Call("POST", $"/api/technician/quotations/{id}/on-site-decision", new { decision = "AGREED", revision = 1 }),
                     new Call("POST", $"/api/technician/quotations/{id}/payment", new { method = "CASH" }), new Call("GET", $"/api/technician/quotations/{id}/payment"),
                     new Call("POST", $"/api/technician/quotations/{id}/payment/cancel"),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Technician);

        // Retired customer decision (410 for the customer, 403 for every other role).
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
        // Staff read-all (2026-11-19): Sales, Technician, Manager and Admin read every contract; the customer has their own routes.
        foreach (var call in new[]
                 {
                     new Call("GET", $"/api/contracts/{id}"), new Call("GET", "/api/contracts"), new Call("GET", $"/api/contracts/{id}/signature-sets"),
                     new Call("GET", $"/api/contracts/{id}/signed-pages/{id}/url"), new Call("GET", $"/api/contracts/{id}/installments/1/payment"),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Manager, RoleCodes.Admin);

        // 30/70 plan: installment payment and paper signature on site (technician only); Manager/Admin support the page upload.
        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/technician/contracts/{id}/installments/1/payment", new { method = "CASH" }),
                     new Call("GET", $"/api/technician/contracts/{id}/installments/1/payment"), new Call("POST", $"/api/technician/contracts/{id}/installments/1/payment/cancel"),
                     new Call("POST", $"/api/technician/contracts/{id}/signature-set", new { expectedPageCount = 2 }),
                     new Call("PUT", $"/api/technician/contracts/{id}/signature-set/pages/1", new { fileId = id }),
                     new Call("POST", $"/api/technician/contracts/{id}/signature-set/finalize"),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Technician);
        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/contracts/{id}/signature-set", new { expectedPageCount = 2 }), new Call("PUT", $"/api/contracts/{id}/signature-set/pages/1", new { fileId = id }),
                     new Call("POST", $"/api/contracts/{id}/signature-set/finalize"),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Manager, RoleCodes.Admin);
        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/customers/me/contracts/{id}/installments/2/payment", new { method = "ONLINE" }),
                     new Call("GET", $"/api/customers/me/contracts/{id}/installments/2/payment"), new Call("POST", $"/api/customers/me/contracts/{id}/installments/2/payment/cancel"),
                     new Call("GET", $"/api/customers/me/contracts/{id}/signature-sets"), new Call("GET", $"/api/customers/me/contracts/{id}/signed-pages/{id}/url"),
                     new Call("POST", $"/api/customers/me/installation-projects/{id}/acceptance", new { decision = "ACCEPT" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Customer);
        // Collection: Sales / Manager / Admin record and escalate; legal review and resolution are Manager / Admin; finance overview is Admin.
        foreach (var call in new[]
                 {
                     new Call("GET", "/api/collections"), new Call("GET", $"/api/collections/{id}"),
                     new Call("POST", $"/api/collections/{id}/activities", new { kind = "CONTACT", summary = "called" }),
                     new Call("POST", $"/api/collections/{id}/escalate", new { reason = "no answer" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin);
        foreach (var call in new[] { new Call("POST", $"/api/collections/{id}/legal-review", new { reason = "x" }), new Call("POST", $"/api/collections/{id}/resolve", new { reason = "x" }) })
            await AssertRolesAsync(factory, call, RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, new Call("GET", "/api/admin/finance/overview"), RoleCodes.Admin);

        // Legacy contract payments: the customer pays or chooses cash; only Manager/Admin read payment history and confirm cash.
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
            // Short-lived read URLs of the signed paper pages (synthetic signer; the real one presigns S3 GETs).
            services.RemoveAll<SmartSolar.Modules.Files.IStorageUrlSigner>();
            services.AddSingleton<SmartSolar.Modules.Files.IStorageUrlSigner>(new FakeStorageUrlSigner());
        });
        _ = factory.Services;   // build the host (schema + seeded roles) before seeding the journey
        var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        var survey = await ctx.QuotableSurveyAsync();
        await ctx.EnsureInstallationWarrantyOptionAsync();
        return new World(factory, storage, payOs, ctx, catalog, survey);
    }

    /// <summary>
    /// Technician cart → issue (finalized, nothing sent to the customer) → the customer's quotation routes answer 410 → the technician records the
    /// customer's on-site agreement, which creates the 30/70 contract at once (DRAFT, readable by the customer before paying the deposit). Returns the
    /// agreed quotation, its contract and its total.
    /// </summary>
    private static async Task<(Guid QuotationId, Guid ContractId, decimal Total)> AgreedThroughTheApiAsync(World w)
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
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/lines", new { revision = issued.GetProperty("revision").GetInt32(),
            productId = w.Catalog.Transport.Id, quantity = 1m }), HttpStatusCode.Conflict, QuotationErrorCodes.NotDraft);

        // The customer's app no longer receives or decides quotations.
        var customer = w.Customer;
        await AssertFailureAsync(await customer.GetAsync("/api/customers/me/quotations"), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/quotations/{quotationId}/accept", null), HttpStatusCode.Gone, "ENDPOINT_RETIRED");

        // The 100% quotation payment is retired (30/70 plan); there is no contract before the agreement.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/payment", new { method = "CASH" }),
            HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        Assert.Empty((await DataOf(await customer.GetAsync("/api/customers/me/contracts"))).EnumerateArray());
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/on-site-decision", new { decision = "MAYBE", revision = issued.GetProperty("revision").GetInt32() }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var agreed = await DataOf(await technician.PostAsJsonAsync($"/api/technician/quotations/{quotationId}/on-site-decision",
            new { decision = "AGREED", revision = issued.GetProperty("revision").GetInt32(), note = "Agreed at the customer's home" }));
        Assert.Equal(QuotationStatuses.Accepted, agreed.GetProperty("status").GetString());
        var contractId = agreed.GetProperty("contractId").GetGuid();
        // The customer previews the full contract (DRAFT) before paying: total, both installments, ownership retention clause.
        var preview = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}"));
        Assert.Equal((ContractStatuses.Draft, total), (preview.GetProperty("status").GetString(), preview.GetProperty("totalAmount").GetDecimal()));
        var plan = preview.GetProperty("paymentPlan");
        Assert.Equal((PaymentPlans.Deposit30Balance70, PaymentPlanStages.DraftCreated), (plan.GetProperty("plan").GetString(), plan.GetProperty("stage").GetString()));
        Assert.Equal(new[] { (1, InstallmentStatuses.Due), (2, InstallmentStatuses.Pending) },
            plan.GetProperty("installments").EnumerateArray().Select(i => (i.GetProperty("sequence").GetInt32(), i.GetProperty("status").GetString()!)));
        Assert.Contains("BẢO LƯU QUYỀN SỞ HỮU", preview.GetProperty("terms").GetString());
        return (quotationId, contractId, total);
    }

    private static decimal Deposit(decimal total) => PaymentPlanCalculator.Split(total)!.Deposit;

    /// <summary>The technician photographs the signed paper (2 pages), uploads them in order and finalizes; returns the finalized set.</summary>
    private static async Task<JsonElement> PaperSignedThroughTheApiAsync(World w, Guid contractId)
    {
        var technician = w.Technician;
        var started = await DataOf(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/signature-set", new { expectedPageCount = 2, note = "signed on site" }));
        Assert.Equal(SignatureSetStatuses.Uploading, started.GetProperty("status").GetString());
        for (var page = 1; page <= 2; page++)
        {
            var file = await UploadAsync(technician, w.Storage, w.Survey.Technician.Id, FilePurposes.SignedContractPage);
            await DataOf(await technician.PutAsJsonAsync($"/api/technician/contracts/{contractId}/signature-set/pages/{page}", new { fileId = file }));
        }
        var finalized = await DataOf(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/signature-set/finalize", new { }));
        Assert.Equal((SignatureSetStatuses.Finalized, 2), (finalized.GetProperty("status").GetString(), finalized.GetProperty("uploadedPageCount").GetInt32()));
        return finalized;
    }

    [Fact]
    public async Task Cash_journey_agreed_on_site_deposit_confirmed_by_a_manager_then_paper_signed_and_installable()
    {
        await using var w = await WorldAsync();
        var (_, contractId, total) = await AgreedThroughTheApiAsync(w);
        var technician = w.Technician;
        var customer = w.Customer;
        var managerId = Guid.NewGuid();
        var manager = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Manager, managerId);
        var deposit = Deposit(total);

        // The balance cannot be paid at the deposit stage; the deposit is opened in cash by the technician.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/2/payment", new { method = "CASH" }),
            HttpStatusCode.Conflict, PaymentPlanErrorCodes.InstallmentNotPayable);
        var cash = await DataOf(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "CASH" }));
        Assert.Equal((InstallmentPaymentStates.AwaitingCashConfirmation, "CASH", deposit), (cash.GetProperty("paymentState").GetString(),
            cash.GetProperty("current").GetProperty("method").GetString(), cash.GetProperty("current").GetProperty("amount").GetDecimal()));
        var attemptId = cash.GetProperty("current").GetProperty("paymentAttemptId").GetGuid();
        Assert.Equal(attemptId, (await DataOf(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "CASH" })))
            .GetProperty("current").GetProperty("paymentAttemptId").GetGuid());
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "ONLINE" }),
            HttpStatusCode.Conflict, PaymentErrorCodes.CashSelected);

        // Neither the customer nor the technician can confirm cash; a wrong amount is refused; the Manager confirms the exact deposit once.
        var confirmBody = new { amount = deposit, receiptReference = "PT-1", evidenceFileId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm", confirmBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm", confirmBody)).StatusCode);
        var evidence = await UploadAsync(manager, w.Storage, managerId, FilePurposes.PaymentEvidence);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm",
            new { amount = total, receiptReference = "PT-1", evidenceFileId = evidence }), HttpStatusCode.Conflict, PaymentErrorCodes.CashAmountMismatch);   // 100% ≠ deposit
        var paid = await DataOf(await manager.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm",
            new { amount = deposit, receiptReference = "PT-1", evidenceFileId = evidence, note = "Collected on site, handed to the cashier" }));
        Assert.Equal(PaymentStatuses.Paid, paid.GetProperty("status").GetString());
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm",
            new { amount = deposit, receiptReference = "PT-2", evidenceFileId = evidence }), HttpStatusCode.Conflict, PaymentErrorCodes.AlreadyPaid);

        // 30% ≠ PAID: the contract waits for the paper signature; no project yet; the legacy 100% payment never applies.
        var contract = await DataOf(await manager.GetAsync($"/api/contracts/{contractId}"));
        Assert.Equal((ContractStatuses.AwaitingSignature, false, PaymentPlanStages.DepositVerified), (contract.GetProperty("status").GetString(),
            contract.GetProperty("paymentCompleted").GetBoolean(), contract.GetProperty("paymentPlan").GetProperty("stage").GetString()));
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ContractNotPaid);
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments/cash", null), HttpStatusCode.Conflict, PaymentErrorCodes.ContractHasInstallments);
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/contracts/{contractId}/payments", null), HttpStatusCode.Conflict, PaymentErrorCodes.ContractHasInstallments);
        // The previous flow's contract steps do not apply to a 30/70 contract.
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/contracts/{contractId}/publish", new { revision = contract.GetProperty("revision").GetInt32() }),
            HttpStatusCode.Conflict, PaymentPlanErrorCodes.NotApplicable);

        await PaperSignedThroughTheApiAsync(w, contractId);
        var signed = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}"));
        Assert.Equal((ContractStatuses.Signed, PaymentPlanStages.ReadyForInstallation, false), (signed.GetProperty("status").GetString(),
            signed.GetProperty("paymentPlan").GetProperty("stage").GetString(), signed.GetProperty("paymentCompleted").GetBoolean()));
        // The customer reads the photographed pages through short-lived signed URLs; another customer cannot.
        var sets = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/signature-sets"));
        var pageFile = sets[0].GetProperty("pages")[0].GetProperty("fileId").GetGuid();
        var url = await customer.GetAsync($"/api/customers/me/contracts/{contractId}/signed-pages/{pageFile}/url");
        Assert.True(url.Headers.CacheControl?.NoStore);
        Assert.StartsWith(FakeStorageUrlSigner.Host, (await DataOf(url)).GetProperty("url").GetString());
        var otherCustomer = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Customer, Guid.NewGuid());
        await AssertFailureAsync(await otherCustomer.GetAsync($"/api/customers/me/contracts/{contractId}/signed-pages/{pageFile}/url"), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);

        var project = await DataOf(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Created);
        Assert.Equal(contractId, project.GetProperty("contractId").GetGuid());
    }

    /// <summary>
    /// The technician records the date agreed in person once the deposit is verified: PLANNED (awaiting the paper signature, not payment), visible
    /// read-only in the customer's app; finalizing the signed pages confirms the schedule and creates the project — no Manager step.
    /// </summary>
    [Fact]
    public async Task Technician_records_the_agreed_date_after_the_deposit_and_the_paper_signature_confirms_it()
    {
        await using var w = await WorldAsync();
        var (_, contractId, total) = await AgreedThroughTheApiAsync(w);
        var customer = w.Customer;
        var managerId = Guid.NewGuid();
        var manager = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Manager, managerId);

        // A Tuesday 1–2 weeks ahead in the scheduling time zone (inside the seeded technician's working week and the planning horizon).
        var local = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date.AddDays(8);
        while (local.DayOfWeek != DayOfWeek.Tuesday) local = local.AddDays(1);
        var starts = new DateTimeOffset(local.AddHours(8), TimeSpan.FromHours(7));
        var plan = new { startsAt = starts, endsAt = starts.AddHours(4), customerAgreementNote = "Thống nhất trực tiếp với khách tại nhà" };
        // Before the deposit the contract cannot be scheduled.
        await AssertFailureAsync(await w.Technician.PostAsJsonAsync($"/api/technicians/me/contracts/{contractId}/installation-schedule", plan),
            HttpStatusCode.Conflict, InstallationScheduleErrorCodes.ContractNotSigned);

        var cash = await DataOf(await w.Technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "CASH" }));
        var evidence = await UploadAsync(manager, w.Storage, managerId, FilePurposes.PaymentEvidence);
        await DataOf(await manager.PostAsJsonAsync($"/api/manager/payments/{cash.GetProperty("current").GetProperty("paymentAttemptId").GetGuid()}/cash/confirm",
            new { amount = Deposit(total), receiptReference = "PT-S1", evidenceFileId = evidence }));

        var stranger = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Technician, Guid.NewGuid());
        await AssertFailureAsync(await stranger.PostAsJsonAsync($"/api/technicians/me/contracts/{contractId}/installation-schedule", plan),
            HttpStatusCode.NotFound, InstallationScheduleErrorCodes.ContractNotFound);
        await AssertFailureAsync(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/installation-slot-offers"), HttpStatusCode.Gone, "ENDPOINT_RETIRED");

        var planned = await DataOf(await w.Technician.PostAsJsonAsync($"/api/technicians/me/contracts/{contractId}/installation-schedule", plan));
        Assert.Equal((InstallationScheduleStatuses.Planned, false, false, true), (planned.GetProperty("status").GetString(), planned.GetProperty("isConfirmed").GetBoolean(),
            planned.GetProperty("awaitingPayment").GetBoolean(), planned.GetProperty("awaitingContractSignature").GetBoolean()));
        var mobile = await DataOf(await customer.GetAsync("/api/customers/me/installation-schedules"));
        Assert.Equal((InstallationScheduleStatuses.Planned, false), (mobile[0].GetProperty("status").GetString(), mobile[0].GetProperty("isConfirmed").GetBoolean()));

        await PaperSignedThroughTheApiAsync(w, contractId);
        var confirmed = await DataOf(await customer.GetAsync($"/api/customers/me/contracts/{contractId}/installation-schedule"));
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (confirmed[0].GetProperty("status").GetString(), confirmed[0].GetProperty("isConfirmed").GetBoolean()));
        var projectId = confirmed[0].GetProperty("projectId").GetGuid();
        var project = await DataOf(await customer.GetAsync($"/api/customers/me/installation-projects/{projectId}"));
        Assert.Equal(InstallationStatuses.Scheduled, project.GetProperty("status").GetString());
        await AssertFailureAsync(await manager.PostAsync($"/api/contracts/{contractId}/installation-project", null), HttpStatusCode.Conflict, InstallationErrorCodes.ProjectExists);
    }

    [Fact]
    public async Task Payos_journey_the_deposit_qr_is_paid_with_a_bank_app_and_a_duplicate_webhook_settles_nothing_twice()
    {
        await using var w = await WorldAsync();
        var (_, contractId, total) = await AgreedThroughTheApiAsync(w);
        var technician = w.Technician;
        var deposit = Deposit(total);

        var online = await DataOf(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "ONLINE" }));
        Assert.Equal(InstallmentPaymentStates.OnlineInProgress, online.GetProperty("paymentState").GetString());
        var current = online.GetProperty("current");
        Assert.Equal("ONLINE", current.GetProperty("method").GetString());
        Assert.False(string.IsNullOrEmpty(current.GetProperty("checkoutUrl").GetString()));   // shown as QR / link on the technician's device
        Assert.Equal(deposit, current.GetProperty("amount").GetDecimal());
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/contracts/{contractId}/installments/1/payment", new { method = "CASH" }),
            HttpStatusCode.Conflict, PaymentErrorCodes.CheckoutInProgress);
        var stranger = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Technician, Guid.NewGuid());
        await AssertFailureAsync(await stranger.GetAsync($"/api/technician/contracts/{contractId}/installments/1/payment"), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);

        var orderCode = current.GetProperty("orderCode").GetInt64();
        var body = w.PayOs.Webhook(orderCode, (long)deposit, "FT-E2E");
        var anonymous = w.Factory.CreateClient();
        var first = await DataOf(await anonymous.PostAsync("/api/payments/payos/webhook", new StringContent(body, System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(WebhookOutcomes.Applied, first.GetProperty("outcome").GetString());
        var repeat = await DataOf(await anonymous.PostAsync("/api/payments/payos/webhook", new StringContent(body, System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(WebhookOutcomes.Duplicate, repeat.GetProperty("outcome").GetString());

        var state = await DataOf(await technician.GetAsync($"/api/technician/contracts/{contractId}/installments/1/payment"));
        Assert.Equal(InstallmentPaymentStates.Paid, state.GetProperty("paymentState").GetString());
        await using var db = w.Factory.CreateDbContext();
        Assert.Equal(1, await db.CustomerContracts.CountAsync());
        Assert.Equal(ContractStatuses.AwaitingSignature, (await db.CustomerContracts.SingleAsync()).Status);
        Assert.Equal(1, await db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await db.PaymentAttemptItems.CountAsync(i => i.ContractInstallmentId != null && i.Status == PaymentItemStatuses.Settled));
        Assert.Null((await db.PaymentAttempts.SingleAsync(a => a.Status == PaymentStatuses.Paid)).ContractId);
        // Staff (any staff role) read the installment state; the directory lists the contract with its debt.
        var sales = CatalogEndpointsTests.ClientFor(w.Factory, RoleCodes.Sales, Guid.NewGuid());
        Assert.Equal(InstallmentPaymentStates.Paid, (await DataOf(await sales.GetAsync($"/api/contracts/{contractId}/installments/1/payment"))).GetProperty("paymentState").GetString());
        var directory = await DataOf(await sales.GetAsync($"/api/contracts?customerId={w.Survey.Request.Customer.Id}"));
        var row = Assert.Single(directory.GetProperty("items").EnumerateArray());
        Assert.Equal((deposit, total - deposit, true), (row.GetProperty("paid").GetDecimal(), row.GetProperty("outstanding").GetDecimal(), row.GetProperty("depositPaid").GetBoolean()));
    }
}
