using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.AfterSales;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// Unified cash receipts through the real API (2026-11-27): an after-sales repair paid in cash — technician photo upload (presigned PAYMENT_EVIDENCE),
/// receipt on <c>/api/technicians/me/after-sales/quotations/{id}/payment/cash-receipt</c>, the shared Manager queue (kind filter, detail, photo,
/// reject → resend → accept), RBAC (technician / Sales / customer / anonymous), envelope and codes. Data up to the cash choice is built with the
/// production handlers on the API's database; storage and presigners are synthetic.
/// </summary>
public sealed class UnifiedCashReceiptEndpointsTests
{
    private static readonly Dictionary<string, string?> Limits = new()
    {
        ["RateLimiting:InstallationWrite:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:ContractRead:PermitLimit"] = "1000", ["RateLimiting:ContractWrite:PermitLimit"] = "1000",
        ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000", ["RateLimiting:FilesWrite:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    private static async Task<Guid> PhotoAsync(HttpClient client, FakeStorageObjectVerifier storage)
    {
        var presign = await DataOf(await client.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PaymentEvidence, fileName = "phieu-thu.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }));
        var objectKey = presign.GetProperty("objectKey").GetString()!;
        storage.Put(Bucket, objectKey, FakeStorageObjectVerifier.JpegBytes);
        var registered = await DataOf(await client.PostAsJsonAsync("/api/files",
            new { purpose = FilePurposes.PaymentEvidence, objectKey, fileName = "phieu-thu.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }),
            HttpStatusCode.Created);
        return registered.GetProperty("fileId").GetGuid();
    }

    [Fact]
    public async Task After_sales_repair_cash_receipt_goes_through_the_shared_manager_queue_with_rbac()
    {
        var storage = new FakeStorageObjectVerifier();
        using var factory = Enabled(storage, new FakeRagServiceClient(), Limits, services =>
        {
            services.RemoveAll<IStorageUrlSigner>();
            services.AddSingleton<IStorageUrlSigner>(new FakeStorageUrlSigner());
            services.RemoveAll<IStorageUploadUrlSigner>();
            services.AddSingleton<IStorageUploadUrlSigner>(new FakeStorageUploadUrlSigner());
        });
        _ = factory.Services;
        await using var ctx = AfterSalesTestContext.CreateOn(factory.CreateDbContext());
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var quotationId = t.Quotation.QuotationId;
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, t.H.TechnicianId);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, t.H.ManagerId);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, t.H.CustomerId);
        var route = $"/api/technicians/me/after-sales/quotations/{quotationId}";

        // The fixture's business clock (quotation validity) is not the API host's wall clock: the cash choice is opened with the production handler at
        // the fixture time and the technician reports when the cash was taken (collectedAt) on that same timeline.
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, quotationId);
        var collectedAt = ctx.Clock.GetUtcNow().AddMinutes(5);
        var before = await DataOf(await technician.GetAsync($"{route}/payment"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("currentCashReceipt").ValueKind);
        Assert.Equal(0, before.GetProperty("cashReceipts").GetArrayLength());

        // Validation and RBAC on the send.
        var invalid = await technician.PostAsJsonAsync($"{route}/payment/cash-receipt", new { paymentAttemptId = attemptId });
        await AssertFailureAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var photo = await PhotoAsync(technician, storage);
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{route}/payment/cash-receipt",
            new { paymentAttemptId = Guid.NewGuid(), receiptNumber = "PT-AS-1", evidenceFileId = photo }), HttpStatusCode.Conflict, "PAYMENT_CASH_RECEIPT_ATTEMPT_MISMATCH");
        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, stranger.Id).PostAsJsonAsync($"{route}/payment/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-AS-1", evidenceFileId = photo }), HttpStatusCode.NotFound, "SERVICE_QUOTATION_NOT_FOUND");
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Manager })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync($"{route}/payment/cash-receipt",
                new { paymentAttemptId = attemptId, receiptNumber = "PT-AS-1", evidenceFileId = photo })).StatusCode);

        // Valid send: SUBMITTED, quotation still ACCEPTED, payment PENDING.
        var sent = await DataOf(await technician.PostAsJsonAsync($"{route}/payment/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-AS-1", evidenceFileId = photo, collectedAt, note = "Khách trả đủ" }));
        Assert.Equal("ACCEPTED", sent.GetProperty("quotationStatus").GetString());
        Assert.Equal("PENDING", sent.GetProperty("current").GetProperty("status").GetString());
        var first = sent.GetProperty("currentCashReceipt");
        Assert.Equal(("SUBMITTED", "SERVICE_QUOTATION"), (first.GetProperty("status").GetString(), first.GetProperty("kind").GetString()));
        var firstId = first.GetProperty("cashReceiptId").GetGuid();
        var own = await technician.GetAsync($"{route}/payment/cash-receipts/{firstId}/evidence/url");
        Assert.True(own.Headers.CacheControl?.NoStore);
        Assert.Equal(photo, (await DataOf(own)).GetProperty("id").GetGuid());
        // While under review: cancel / switch refused.
        await AssertFailureAsync(await technician.PostAsync($"{route}/payment/cancel", null), HttpStatusCode.Conflict, "PAYMENT_CASH_RECEIPT_UNDER_REVIEW");
        Assert.Equal("PAYMENT_CASH_SELECTED", (await ctx.Quotations().StartPaymentOnSiteAsync(t.H.TechnicianId, quotationId, "ONLINE", CancellationToken.None)).ErrorCode);
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/after-sales/quotations/{quotationId}/cash/cancel", null), HttpStatusCode.Conflict,
            "PAYMENT_CASH_RECEIPT_UNDER_REVIEW");
        // The customer reads the payment status without staff evidence.
        var customerPayment = await DataOf(await customer.GetAsync($"/api/customers/me/after-sales/payments/{attemptId}"));
        Assert.Equal("PENDING", customerPayment.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, customerPayment.GetProperty("evidenceFileId").ValueKind);
        Assert.False(customerPayment.TryGetProperty("cashReceipts", out _));

        // Shared queue: kind filter, after-sales context, detail with lines, the technician's photo.
        var queue = await DataOf(await manager.GetAsync("/api/manager/payments/cash-receipts?status=SUBMITTED&kind=SERVICE_QUOTATION"));
        var row = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("AFTER_SALES_QUOTATION", row.GetProperty("context").GetString());
        Assert.Equal(t.RequestId, row.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal("FAULT", row.GetProperty("serviceRequestKind").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("installmentSequence").ValueKind);
        Assert.Equal(0, (await DataOf(await manager.GetAsync("/api/manager/payments/cash-receipts?kind=INSTALLMENT"))).GetProperty("items").GetArrayLength());
        await AssertFailureAsync(await manager.GetAsync("/api/manager/payments/cash-receipts?kind=REPAIR"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var detail = await DataOf(await manager.GetAsync($"/api/manager/payments/cash-receipts/{firstId}"));
        Assert.Equal("REPAIR_QUOTATION", detail.GetProperty("lines")[0].GetProperty("itemType").GetString());
        Assert.Equal(photo, (await DataOf(await manager.GetAsync($"/api/manager/payments/cash-receipts/{firstId}/evidence/url?disposition=inline"))).GetProperty("id").GetGuid());
        // Sales / technician / customer / anonymous never review.
        foreach (var role in new[] { RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync($"/api/manager/payments/cash-receipts/{firstId}/accept",
                new { revision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/manager/payments/cash-receipts")).StatusCode);

        // Reject → resend → accept; work authorized; Admin can review too.
        await DataOf(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{firstId}/reject", new { revision = 1, reason = "Ảnh mờ" }));
        var afterReject = await DataOf(await technician.GetAsync($"{route}/payment"));
        Assert.Equal(("REJECTED", "Ảnh mờ"), (afterReject.GetProperty("currentCashReceipt").GetProperty("status").GetString(),
            afterReject.GetProperty("currentCashReceipt").GetProperty("reviewReason").GetString()));
        var resent = (await DataOf(await technician.PostAsJsonAsync($"{route}/payment/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-AS-1", evidenceFileId = await PhotoAsync(technician, storage), collectedAt }))).GetProperty("currentCashReceipt");
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var accepted = await DataOf(await admin.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{resent.GetProperty("cashReceiptId").GetGuid()}/accept",
            new { revision = resent.GetProperty("revision").GetInt32() }));
        Assert.Equal(("PAID", "PT-AS-1"), (accepted.GetProperty("status").GetString(), accepted.GetProperty("providerReference").GetString()));
        var paid = await DataOf(await technician.GetAsync($"{route}/payment"));
        Assert.Equal(("PAID", "ACCEPTED", 2), (paid.GetProperty("quotationStatus").GetString(), paid.GetProperty("currentCashReceipt").GetProperty("status").GetString(),
            paid.GetProperty("cashReceipts").GetArrayLength()));
        Assert.Equal("READY_FOR_WORK", (await ctx.Db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == t.RequestId)).Status);
    }
}
