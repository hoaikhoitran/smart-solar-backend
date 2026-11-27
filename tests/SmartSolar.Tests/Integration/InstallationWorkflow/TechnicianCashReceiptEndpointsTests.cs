using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Flow 2 technician cash receipt through the real API (2026-11-27): camera photo uploaded with the normal presigned upload (PAYMENT_EVIDENCE),
/// receipt sent with its number, Manager queue / detail / photo URL, reject with reason → resend → accept; RBAC (the technician can never confirm),
/// envelope and error codes. The agreement and the cash choice are built with the production handlers on the API's database; storage is synthetic.
/// </summary>
public sealed class TechnicianCashReceiptEndpointsTests
{
    private static readonly Dictionary<string, string?> Limits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["RateLimiting:ContractRead:PermitLimit"] = "1000", ["RateLimiting:ContractWrite:PermitLimit"] = "1000",
        ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000", ["RateLimiting:FilesWrite:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    private static (AuthApiFactory Factory, FakeStorageObjectVerifier Storage) Start()
    {
        var storage = new FakeStorageObjectVerifier();
        var factory = Enabled(storage, new FakeRagServiceClient(), Limits, services =>
        {
            services.RemoveAll<IStorageUrlSigner>();
            services.AddSingleton<IStorageUrlSigner>(new FakeStorageUrlSigner());
            services.RemoveAll<IStorageUploadUrlSigner>();
            services.AddSingleton<IStorageUploadUrlSigner>(new FakeStorageUploadUrlSigner());
        });
        return (factory, storage);
    }

    /// <summary>The FE camera flow: presigned PUT URL for a JPEG, PUT (simulated), register → VERIFIED file id.</summary>
    private static async Task<Guid> PhotoAsync(HttpClient client, FakeStorageObjectVerifier storage)
    {
        var presign = await DataOf(await client.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PaymentEvidence, fileName = "phieu-thu.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }));
        var objectKey = presign.GetProperty("objectKey").GetString()!;
        storage.Put(Bucket, objectKey, FakeStorageObjectVerifier.JpegBytes);
        var registered = await DataOf(await client.PostAsJsonAsync("/api/files",
            new { purpose = FilePurposes.PaymentEvidence, objectKey, fileName = "phieu-thu.jpg", contentType = FilePolicies.Jpeg, sizeBytes = FakeStorageObjectVerifier.JpegBytes.Length }),
            HttpStatusCode.Created);
        Assert.Equal(FileStatuses.Verified, registered.GetProperty("status").GetString());
        return registered.GetProperty("fileId").GetGuid();
    }

    [Fact]
    public async Task Technician_sends_the_receipt_manager_rejects_then_accepts_and_only_staff_can_confirm()
    {
        var (factory, storage) = Start();
        using var host = factory;
        _ = factory.Services;
        await using var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx, "Receipt customer");
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, flow.TechnicianId);
        var managerId = flow.Manager.Id;
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, managerId);
        var route = $"/api/technician/contracts/{flow.ContractId}/installments/1/payment";

        var cash = await DataOf(await technician.PostAsJsonAsync(route, new { method = "CASH" }));
        var attemptId = cash.GetProperty("current").GetProperty("paymentAttemptId").GetGuid();
        Assert.Equal(JsonValueKind.Null, cash.GetProperty("currentCashReceipt").ValueKind);
        Assert.Equal(0, cash.GetProperty("cashReceipts").GetArrayLength());

        // Validation envelope: missing photo and number → 400 with field errors.
        var invalid = await technician.PostAsJsonAsync($"{route}/cash-receipt", new { paymentAttemptId = attemptId });
        await AssertFailureAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var details = (await BodyOf(invalid)).GetProperty("error").GetProperty("details");
        Assert.True(details.TryGetProperty("receiptNumber", out _));
        Assert.True(details.TryGetProperty("evidenceFileId", out _));
        // A photo that is not the technician's (upload failed / another user's file) → FILE_* codes.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{route}/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-0001", evidenceFileId = Guid.NewGuid() }), HttpStatusCode.NotFound, FileErrorCodes.NotFound);
        // The upload itself keeps its type rules (a GIF is refused at the presigned step).
        await AssertFailureAsync(await technician.PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PaymentEvidence, fileName = "a.gif", contentType = "image/gif", sizeBytes = 10 }), HttpStatusCode.BadRequest, FileErrorCodes.TypeNotAllowed);
        // A customer cannot upload payment evidence.
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, flow.CustomerId).PostAsJsonAsync("/api/files/presigned-upload",
            new { purpose = FilePurposes.PaymentEvidence, fileName = "a.jpg", contentType = FilePolicies.Jpeg, sizeBytes = 10 }), HttpStatusCode.Forbidden, FileErrorCodes.UploadNotAllowed);

        // Valid receipt: still not paid.
        var photo = await PhotoAsync(technician, storage);
        var sent = await DataOf(await technician.PostAsJsonAsync($"{route}/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-0001", evidenceFileId = photo, note = "Khách trả đủ" }));
        Assert.Equal("AWAITING_CASH_CONFIRMATION", sent.GetProperty("paymentState").GetString());
        Assert.Equal("DUE", sent.GetProperty("installmentStatus").GetString());
        var first = sent.GetProperty("currentCashReceipt");
        Assert.Equal("SUBMITTED", first.GetProperty("status").GetString());
        Assert.Equal("PT-0001", first.GetProperty("receiptNumber").GetString());
        Assert.Equal(flow.TechnicianId, first.GetProperty("collectedByUserId").GetGuid());
        var firstId = first.GetProperty("cashReceiptId").GetGuid();
        // A second one while under review → 409.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{route}/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-0002", evidenceFileId = await PhotoAsync(technician, storage) }), HttpStatusCode.Conflict,
            "PAYMENT_CASH_RECEIPT_ALREADY_SUBMITTED");
        // The technician sees their own photo; another technician gets 404.
        var own = await DataOf(await technician.GetAsync($"{route}/cash-receipts/{firstId}/evidence/url"));
        Assert.Equal(photo, own.GetProperty("id").GetGuid());
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        var strangerClient = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, stranger.Id);
        await AssertFailureAsync(await strangerClient.GetAsync($"{route}/cash-receipts/{firstId}/evidence/url"), HttpStatusCode.NotFound, "CONTRACT_NOT_FOUND");
        await AssertFailureAsync(await strangerClient.PostAsJsonAsync($"{route}/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-0003", evidenceFileId = photo }), HttpStatusCode.NotFound, "CONTRACT_NOT_FOUND");

        // The technician can never review or confirm: the Manager endpoints are staff-only.
        foreach (var call in new[] { $"/api/manager/payments/cash-receipts/{firstId}/accept", $"/api/manager/payments/cash-receipts/{firstId}/reject" })
            Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync(call, new { revision = 1, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync($"/api/manager/payments/{attemptId}/cash/confirm",
            new { amount = 1, receiptReference = "PT-0001", evidenceFileId = photo })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync("/api/manager/payments/cash-receipts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/manager/payments/cash-receipts")).StatusCode);
        // Nor can the cash choice be cancelled while under review.
        await AssertFailureAsync(await technician.PostAsync($"{route}/cancel", null), HttpStatusCode.Conflict, "PAYMENT_CASH_RECEIPT_UNDER_REVIEW");

        // Manager queue / detail / photo without re-upload.
        var queue = await DataOf(await manager.GetAsync("/api/manager/payments/cash-receipts?status=SUBMITTED&page=1&pageSize=20"));
        Assert.Equal(1, queue.GetProperty("totalItems").GetInt32());
        var row = queue.GetProperty("items")[0];
        Assert.Equal(firstId, row.GetProperty("receipt").GetProperty("cashReceiptId").GetGuid());
        Assert.Equal(1, row.GetProperty("installmentSequence").GetInt32());
        Assert.Equal("DEPOSIT", row.GetProperty("installmentKind").GetString());
        Assert.Equal("Receipt customer", row.GetProperty("customerName").GetString());
        Assert.Equal("PENDING", row.GetProperty("paymentStatus").GetString());
        await AssertFailureAsync(await manager.GetAsync("/api/manager/payments/cash-receipts?status=NOPE"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var detail = await DataOf(await manager.GetAsync($"/api/manager/payments/cash-receipts/{firstId}"));
        Assert.Equal(1, detail.GetProperty("history").GetArrayLength());
        var url = await manager.GetAsync($"/api/manager/payments/cash-receipts/{firstId}/evidence/url?disposition=inline");
        Assert.True(url.Headers.CacheControl?.NoStore);
        Assert.Equal(photo, (await DataOf(url)).GetProperty("id").GetGuid());
        await AssertFailureAsync(await manager.GetAsync($"/api/manager/payments/cash-receipts/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "PAYMENT_CASH_RECEIPT_NOT_FOUND");

        // Reject: reason required; then rejected; payment still open.
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{firstId}/reject", new { revision = 1 }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{firstId}/reject", new { reason = "x" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var rejected = await DataOf(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{firstId}/reject",
            new { revision = 1, reason = "Ảnh mờ, không đọc được số phiếu" }));
        Assert.Equal("REJECTED", rejected.GetProperty("status").GetString());
        var afterReject = await DataOf(await technician.GetAsync(route));
        Assert.Equal("REJECTED", afterReject.GetProperty("currentCashReceipt").GetProperty("status").GetString());
        Assert.Equal("Ảnh mờ, không đọc được số phiếu", afterReject.GetProperty("currentCashReceipt").GetProperty("reviewReason").GetString());
        Assert.Equal("DUE", afterReject.GetProperty("installmentStatus").GetString());

        // Resend with a new photo; accept with a stale revision → 409; accept → PAID.
        var resent = await DataOf(await technician.PostAsJsonAsync($"{route}/cash-receipt",
            new { paymentAttemptId = attemptId, receiptNumber = "PT-0001", evidenceFileId = await PhotoAsync(technician, storage) }));
        var second = resent.GetProperty("currentCashReceipt");
        var secondId = second.GetProperty("cashReceiptId").GetGuid();
        Assert.Equal(2, resent.GetProperty("cashReceipts").GetArrayLength());
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{secondId}/accept", new { revision = 99 }), HttpStatusCode.Conflict,
            "PAYMENT_CASH_RECEIPT_STALE");
        var accepted = await DataOf(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{secondId}/accept",
            new { revision = second.GetProperty("revision").GetInt32(), note = "Đã đối chiếu" }));
        Assert.Equal("PAID", accepted.GetProperty("status").GetString());
        Assert.Equal("CASH", accepted.GetProperty("method").GetString());
        Assert.Equal("PT-0001", accepted.GetProperty("providerReference").GetString());
        Assert.Equal(managerId, accepted.GetProperty("confirmedByUserId").GetGuid());
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/cash-receipts/{secondId}/accept", new { revision = 2 }), HttpStatusCode.Conflict,
            "PAYMENT_CASH_RECEIPT_NOT_REVIEWABLE");
        var paid = await DataOf(await technician.GetAsync(route));
        Assert.Equal("PAID", paid.GetProperty("paymentState").GetString());
        Assert.Equal("ACCEPTED", paid.GetProperty("currentCashReceipt").GetProperty("status").GetString());
        // The customer sees the paid installment, never the review data.
        var customer = await DataOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, flow.CustomerId)
            .GetAsync($"/api/customers/me/contracts/{flow.ContractId}/installments/1/payment"));
        Assert.Equal("PAID", customer.GetProperty("paymentState").GetString());
        Assert.Equal(JsonValueKind.Null, customer.GetProperty("cashReceipts").ValueKind);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == "PAID"));
    }
}
