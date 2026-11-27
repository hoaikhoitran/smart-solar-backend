using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Unified technician cash receipts for chargeable after-sales quotations (2026-11-27): repairs, warranty / disaster remainders, one-time maintenance,
/// fault work found during package maintenance. Same receipt rules and Manager queue as the installation installments; the existing after-sales
/// settlement applies the accepted cash (100 % of the customer amount). Nothing due → no payment, no receipt. Production handlers on SQLite.
/// </summary>
public sealed class UnifiedCashReceiptTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ helpers

    internal sealed record Quoted(HandedOver H, Guid RequestId, IReadOnlyList<Guid> Items, ServiceQuotationView Quotation);

    /// <summary>A FAULT with the given coverages; the technician prices every chargeable item (breaker 150,000 + labour 300,000 per item).</summary>
    internal static async Task<Quoted> TechnicianQuotedAsync(AfterSalesTestContext ctx, params string[] coverages)
    {
        var (h, requestId, items) = await DecidedAsync(ctx, coverages.Length == 0 ? [ItemCoverages.Chargeable] : coverages);
        var breaker = await CatalogAsync(ctx, $"BRK-{Guid.NewGuid():N}"[..12], "BREAKER", 150_000m);
        var labor = await CatalogAsync(ctx, $"SVC-{Guid.NewGuid():N}"[..12], ProductTypes.OtherService, 300_000m, "JOB");
        var priced = items.Where((_, i) => (coverages.Length == 0 ? ItemCoverages.Chargeable : coverages[i]) == ItemCoverages.Chargeable).ToList();
        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput(priced.Select(id => Line(id, (breaker.Id, 1), (labor.Id, 1))).ToList(), null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        return new Quoted(h, requestId, items, issued.Value!.Quotations.Single(q => QuotationStatuses.IsOpen(q.Status)));
    }

    /// <summary>The technician records AGREED and opens CASH: the pending attempt id.</summary>
    internal static async Task<Guid> CashOpenedAsync(AfterSalesTestContext ctx, Guid technicianId, Guid quotationId)
    {
        var agreed = await ctx.Quotations().RecordOnSiteDecisionAsync(technicianId, quotationId, ServiceOnSiteDecisions.Agreed, null, None);
        Assert.True(agreed.Succeeded, agreed.Message);
        var cash = await ctx.Quotations().StartPaymentOnSiteAsync(technicianId, quotationId, "CASH", None);
        Assert.True(cash.Succeeded, cash.Message);
        Assert.Equal((PaymentMethods.Cash, PaymentStatuses.Pending), (cash.Value!.Method, cash.Value.Status));
        return cash.Value.PaymentAttemptId;
    }

    internal static Task<OperationResult<ServiceQuotationPaymentView>> SendAsync(AfterSalesTestContext ctx, Guid technicianId, Guid quotationId, Guid attemptId,
        string? number, Guid fileId, DateTimeOffset? collectedAt = null)
        => ctx.Quotations().SubmitCashReceiptOnSiteAsync(technicianId, quotationId, new CashReceiptInput(attemptId, number, fileId, collectedAt, "Khách trả tiền mặt"), None);

    internal static async Task<CashReceiptView> SentAsync(AfterSalesTestContext ctx, Guid technicianId, Guid quotationId, Guid attemptId, string? number = null,
        DateTimeOffset? collectedAt = null)
    {
        var photo = await ctx.Installation.VerifiedFileAsync(technicianId, FilePurposes.PaymentEvidence);
        var sent = await SendAsync(ctx, technicianId, quotationId, attemptId, number ?? Number(), photo, collectedAt);
        Assert.True(sent.Succeeded, sent.ErrorCode + " " + sent.Message);
        Assert.Equal(CashReceiptStatuses.Submitted, sent.Value!.CurrentCashReceipt!.Status);
        return sent.Value.CurrentCashReceipt;
    }

    private static string Number() => $"PT-{Guid.NewGuid():N}"[..20];

    private static DiagnosedItemInput Item(string description) => new(null, description, $"{description}: needs work", null);

    private static async Task AcceptedAsync(AfterSalesTestContext ctx, Guid managerId, CashReceiptView receipt)
    {
        var accepted = await ctx.Payments().AcceptCashReceiptAsync(managerId, receipt.CashReceiptId, receipt.Revision, null, None);
        Assert.True(accepted.Succeeded, accepted.ErrorCode + " " + accepted.Message);
        Assert.Equal(PaymentStatuses.Paid, accepted.Value!.Status);
    }

    private static async Task<string> RequestStatusAsync(AfterSalesTestContext ctx, Guid requestId) => (await ctx.Manager().GetAsync(requestId, None)).Value!.Status;

    // ------------------------------------------------------------------ 1. paid repair, CASH

    [Fact]
    public async Task UC01_paid_repair_in_cash_waits_for_review_then_the_accepted_receipt_authorizes_the_work()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-REPAIR-01");

        // Sending is not paying.
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == t.Quotation.QuotationId)).Status);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        Assert.NotEqual(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, t.RequestId));
        Assert.Equal((CashReceiptKinds.ServiceQuotation, (Guid?)t.Quotation.QuotationId, (Guid?)t.RequestId, (Guid?)null, (Guid?)null),
            (receipt.Kind, receipt.ServiceQuotationId, receipt.ServiceRequestId, receipt.ContractId, receipt.ContractInstallmentId));
        Assert.Equal(450_000m, receipt.Amount);   // the backend amount (100 % of the customer amount)
        Assert.Single(await ctx.Db.AfterSalesEvents.AsNoTracking().Where(e => e.SubjectId == t.RequestId && e.EventType == "CASH_RECEIPT_SUBMITTED").ToListAsync());

        // One queue: the after-sales row carries its request context.
        var row = Assert.Single((await ctx.Payments().PageCashReceiptsAsync(CashReceiptStatuses.Submitted, new PageRequest(1, 20), None, CashReceiptKinds.ServiceQuotation)).Items);
        Assert.Equal(CashReceiptContexts.AfterSalesQuotation, row.Context);
        Assert.Equal((t.RequestId, t.Quotation.Version, ServiceRequestKinds.Fault), (row.ServiceRequestId!.Value, row.QuotationVersion!.Value, row.ServiceRequestKind));
        Assert.False(string.IsNullOrEmpty(row.ServiceRequestNumber));
        Assert.Equal(t.H.CustomerId, row.CustomerUserId);
        Assert.Null(row.InstallmentSequence);
        Assert.Empty((await ctx.Payments().PageCashReceiptsAsync(null, new PageRequest(1, 20), None, CashReceiptKinds.Installment)).Items);

        await AcceptedAsync(ctx, t.H.ManagerId, receipt);
        var quotation = await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == t.Quotation.QuotationId);
        Assert.Equal(QuotationStatuses.Paid, quotation.Status);
        Assert.Equal(receipt.CollectedAt, quotation.PaidAt);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, t.RequestId));
        var attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        Assert.Equal(("PT-REPAIR-01", (Guid?)receipt.EvidenceFileId, (Guid?)t.H.ManagerId), (attempt.ProviderReference, attempt.EvidenceFileId, attempt.ConfirmedByUserId));
        Assert.All(await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == attemptId).ToListAsync(), i => Assert.Equal(PaymentItemStatuses.Settled, i.Status));
        var detail = (await ctx.Payments().GetCashReceiptForStaffAsync(receipt.CashReceiptId, None)).Value!;
        Assert.Equal(PaymentItemStatuses.Settled, Assert.Single(detail.Lines!).Status);
        var view = (await ctx.Quotations().GetPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, None)).Value!;
        Assert.Equal((CashReceiptStatuses.Accepted, 1), (view.CurrentCashReceipt!.Status, view.CashReceipts!.Count));
        // No second receipt, no second confirmation.
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await SendAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, Number(),
            await ctx.Installation.VerifiedFileAsync(t.H.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None)).ErrorCode);
    }

    // ------------------------------------------------------------------ 2 + 25. PayOS unchanged

    [Fact]
    public async Task UC02_paid_repair_through_payos_never_takes_a_receipt_and_the_webhook_still_settles_once()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(t.H.TechnicianId, t.Quotation.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var link = (await ctx.Quotations().StartPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, "ONLINE", None)).Value!;
        var photo = await ctx.Installation.VerifiedFileAsync(t.H.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await SendAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, link.PaymentAttemptId, Number(), photo)).ErrorCode);
        // Reconciliation (server-to-server) of an unpaid link changes nothing; the verified webhook settles once.
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Payments().ReconcileAsync(link.PaymentAttemptId, force: true, None))!.Status);
        var webhook = ctx.Installation.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-UC02");
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, t.RequestId));
        var view = (await ctx.Quotations().GetPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, None)).Value!;
        Assert.Empty(view.CashReceipts!);
        Assert.Null(view.CurrentCashReceipt);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
    }

    // ------------------------------------------------------------------ 3 + 7. nothing due

    [Fact]
    public async Task UC03_UC07_fully_covered_warranty_and_disaster_work_creates_no_payment_and_takes_no_receipt()
    {
        // Warranty: free items need no quotation at all.
        await using var ctx = Create();
        var (h, _, _) = await DecidedAsync(ctx, ItemCoverages.InstallationWarranty);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));

        // Disaster package covering 100 %: the agreed quotation is authorized at once (no 0 VND checkout) and a receipt is refused.
        await using var storm = Create();
        var hs = await storm.HandedOverAsync();
        var term = await storm.ActiveTermAsync(hs, await storm.PackageAsync(hs.AdminId, ServicePackageKinds.DisasterProtection, percent: 100m,
            perIncident: 50_000_000m, termLimit: 80_000_000m));
        storm.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var fault = await storm.FaultAsync(hs, "Storm broke panels", disasterType: DisasterTypes.Storm, incidentAt: storm.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await storm.Manager().DecideClaimAsync(hs.ManagerId, fault.RequestId, true, "confirmed", None)).Succeeded);
        var items = await storm.InspectedAsync(hs, fault.RequestId, storm.Clock.GetUtcNow().AddDays(2), Item("Panels"));
        await storm.DecideAsync(hs, fault.RequestId, (items[0], ItemCoverages.DisasterProtection));
        var panel = await CatalogAsync(storm, "PNL-FULL", ProductTypes.SolarPanel, 2_000_000m);
        var issued = await storm.Quotations().IssueByTechnicianAsync(hs.TechnicianId, fault.RequestId, new TechnicianQuotationInput([Line(items[0], (panel.Id, 2))], null, null), None);
        var q = Assert.Single(issued.Value!.Quotations);
        Assert.Equal((4_000_000m, 0m), (q.DisasterSupportAmount, q.CustomerAmount));
        Assert.True((await storm.Quotations().RecordOnSiteDecisionAsync(hs.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        Assert.Equal(QuotationStatuses.Paid, (await storm.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await storm.Quotations().StartPaymentOnSiteAsync(hs.TechnicianId, q.QuotationId, "CASH", None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await SendAsync(storm, hs.TechnicianId, q.QuotationId, Guid.NewGuid(), Number(),
            await storm.Installation.VerifiedFileAsync(hs.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(0, await storm.Db.PaymentAttemptItems.CountAsync(i => i.QuotationId == q.QuotationId));   // no 0 VND checkout
        Assert.Equal(0, await storm.Db.CashReceiptSubmissions.CountAsync());
        Assert.True((await storm.Quotations().GetPaymentOnSiteAsync(hs.TechnicianId, q.QuotationId, None)).Value!.NothingToPay);
    }

    // ------------------------------------------------------------------ 4. partial warranty

    [Fact]
    public async Task UC04_a_partly_warranted_repair_charges_only_the_customer_part_through_the_receipt()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx, ItemCoverages.InstallationWarranty, ItemCoverages.Chargeable);
        Assert.Equal(450_000m, t.Quotation.CustomerAmount);   // the warranted item is not priced
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        Assert.Equal(450_000m, receipt.Amount);
        await AcceptedAsync(ctx, t.H.ManagerId, receipt);
        Assert.Equal(ItemStatuses.Authorized, (await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.Id == t.Items[0])).Status);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
    }

    // ------------------------------------------------------------------ 5. one-time maintenance

    [Fact]
    public async Task UC05_one_time_maintenance_paid_in_cash_through_the_receipt()
    {
        await using var ctx = Create();
        var m = await OneTimeMaintenanceTests.InspectedAsync(ctx);
        var q = await OneTimeMaintenanceTests.PricedAsync(ctx, m);
        var attemptId = await CashOpenedAsync(ctx, m.H.TechnicianId, q.QuotationId);
        Assert.Equal(PaymentItemTypes.MaintenanceQuotation, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.PaymentAttemptId == attemptId)).ItemType);
        var receipt = await SentAsync(ctx, m.H.TechnicianId, q.QuotationId, attemptId);
        Assert.Equal(q.CustomerAmount, receipt.Amount);
        Assert.Equal(ServiceRequestKinds.OneTimeMaintenance,
            Assert.Single((await ctx.Payments().PageCashReceiptsAsync(null, new PageRequest(1, 20), None)).Items).ServiceRequestKind);
        await AcceptedAsync(ctx, m.H.ManagerId, receipt);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(x => x.Id == q.QuotationId)).Status);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, m.RequestId));
    }

    // ------------------------------------------------------------------ 6. package maintenance with chargeable work found

    [Fact]
    public async Task UC06_fault_work_found_during_a_package_maintenance_visit_is_paid_through_the_receipt_and_the_package_visit_stays_free()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, visitMonths: "1,7"));
        var month1 = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id && e.PlanMonth == 1);
        var booking = (await ctx.Customer().BookMaintenanceAsync(h.CustomerId, month1.Id, Later(10), null, None)).Value!;
        var visit = await ctx.VisitStartedAsync(h, booking.RequestId, Later(10));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, visit, "Cleaned panels; cracked junction box found", [], [], null, None)).Succeeded);
        var fault = await ctx.Customer().ReportFaultFromMaintenanceAsync(h.CustomerId, booking.RequestId, "Junction box cracked (found during maintenance)", None);
        Assert.True(fault.Succeeded, fault.Message);
        var items = await ctx.InspectedAsync(h, fault.Value!.RequestId, ctx.Clock.GetUtcNow().AddDays(12), Item("Junction box"));
        await ctx.DecideAsync(h, fault.Value.RequestId, (items[0], ItemCoverages.Chargeable));
        var box = await CatalogAsync(ctx, "JB-IP68", "JUNCTION_BOX", 120_000m);
        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, fault.Value.RequestId, new TechnicianQuotationInput([Line(items[0], (box.Id, 1))], null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        var q = Assert.Single(issued.Value!.Quotations);
        var attemptId = await CashOpenedAsync(ctx, h.TechnicianId, q.QuotationId);
        var receipt = await SentAsync(ctx, h.TechnicianId, q.QuotationId, attemptId);
        Assert.Equal(120_000m, receipt.Amount);
        await AcceptedAsync(ctx, h.ManagerId, receipt);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(x => x.Id == q.QuotationId)).Status);
        // The package visit itself never got a quotation or a payment.
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync(x => x.RequestId == booking.RequestId));
    }

    // ------------------------------------------------------------------ 8 + 9. disaster

    [Fact]
    public async Task UC08_partial_disaster_coverage_customer_part_in_cash_keeps_the_reserved_support()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));
        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var fault = await ctx.FaultAsync(h, "Storm broke panels", disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await ctx.Manager().DecideClaimAsync(h.ManagerId, fault.RequestId, true, "confirmed", None)).Succeeded);
        var items = await ctx.InspectedAsync(h, fault.RequestId, ctx.Clock.GetUtcNow().AddDays(2), Item("Panels"));
        await ctx.DecideAsync(h, fault.RequestId, (items[0], ItemCoverages.DisasterProtection));
        var panel = await CatalogAsync(ctx, "PNL-REPL", ProductTypes.SolarPanel, 2_000_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, fault.RequestId,
            new TechnicianQuotationInput([Line(items[0], (panel.Id, 2))], null, null), None)).Value!.Quotations);
        Assert.Equal((2_800_000m, 1_200_000m), (q.DisasterSupportAmount, q.CustomerAmount));
        var attemptId = await CashOpenedAsync(ctx, h.TechnicianId, q.QuotationId);
        var receipt = await SentAsync(ctx, h.TechnicianId, q.QuotationId, attemptId);
        Assert.Equal(1_200_000m, receipt.Amount);   // only the customer part
        await AcceptedAsync(ctx, h.ManagerId, receipt);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2_800_000m, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).SupportReserved);   // unchanged by the cash
    }

    [Fact]
    public async Task UC09_a_rejected_disaster_claim_followed_by_an_accepted_paid_repair_in_cash()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));
        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var fault = await ctx.FaultAsync(h, "Storm?", disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await ctx.Manager().DecideClaimAsync(h.ManagerId, fault.RequestId, false, "No storm damage visible", None)).Succeeded);
        var items = await ctx.InspectedAsync(h, fault.RequestId, ctx.Clock.GetUtcNow().AddDays(2), Item("Panel"));
        await ctx.DecideAsync(h, fault.RequestId, (items[0], ItemCoverages.Chargeable));
        var panel = await CatalogAsync(ctx, "PNL-PAID", ProductTypes.SolarPanel, 2_000_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, fault.RequestId,
            new TechnicianQuotationInput([Line(items[0], (panel.Id, 1))], null, null), None)).Value!.Quotations);
        Assert.Equal((0m, 2_000_000m), (q.DisasterSupportAmount, q.CustomerAmount));
        var attemptId = await CashOpenedAsync(ctx, h.TechnicianId, q.QuotationId);
        await AcceptedAsync(ctx, h.ManagerId, await SentAsync(ctx, h.TechnicianId, q.QuotationId, attemptId));
        Assert.Equal(0m, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).SupportReserved);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, fault.RequestId));
    }

    // ------------------------------------------------------------------ 10–15. validation / RBAC / duplicates

    [Fact]
    public async Task UC10_UC11_missing_or_invalid_photo_and_number_are_refused_and_nothing_is_stored()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var tech = t.H.TechnicianId;
        var photo = await ctx.Installation.VerifiedFileAsync(tech, FilePurposes.PaymentEvidence);
        async Task FieldAsync(string field, Task<OperationResult<ServiceQuotationPaymentView>> call)
        {
            var r = await call;
            Assert.Equal("VALIDATION_FAILED", r.ErrorCode);
            Assert.Contains(field, ((IDictionary<string, string[]>)r.Details!).Keys);
        }
        await FieldAsync("evidenceFileId", SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, Number(), Guid.Empty));
        await FieldAsync("receiptNumber", SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, " ", photo));
        await FieldAsync("collectedAt", SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, Number(), photo, ctx.Clock.GetUtcNow().AddHours(2)));
        Assert.Equal(FileErrorCodes.NotVerified, (await SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, Number(),
            await ctx.Installation.UnverifiedFileAsync(tech, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned, (await SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, Number(),
            await ctx.Installation.VerifiedFileAsync(t.H.ManagerId, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(FileErrorCodes.WrongPurpose, (await SendAsync(ctx, tech, t.Quotation.QuotationId, attemptId, Number(),
            await ctx.Installation.VerifiedFileAsync(tech, FilePurposes.ServiceEvidence))).ErrorCode);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
        Assert.Equal(15 * 1024 * 1024, FilePolicies.ByPurpose[FilePurposes.PaymentEvidence].MaxSizeBytes);   // limit unchanged
    }

    [Fact]
    public async Task UC12_UC13_unassigned_or_reassigned_technicians_and_wrong_attempts_are_refused()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        var strangerPhoto = await ctx.Installation.VerifiedFileAsync(stranger.Id, FilePurposes.PaymentEvidence);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await SendAsync(ctx, stranger.Id, t.Quotation.QuotationId, attemptId, Number(), strangerPhoto)).ErrorCode);
        var photo = await ctx.Installation.VerifiedFileAsync(t.H.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.Equal(CashReceiptErrorCodes.AttemptMismatch, (await SendAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, Guid.NewGuid(), Number(), photo)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await SendAsync(ctx, t.H.TechnicianId, Guid.NewGuid(), attemptId, Number(), photo)).ErrorCode);

        // An installment attempt id can never be attached to a quotation receipt (context of the payment lines).
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        Assert.Equal(CashReceiptErrorCodes.ContextMismatch, (await ctx.Payments().SubmitCashReceiptAsync(t.H.TechnicianId,
            CashReceiptContext.ForInstallment(t.H.ContractId, Guid.NewGuid()), attemptId, new CashReceiptInput(attemptId, Number(), photo, null, null), None)).ErrorCode);
        // The technician reads their receipt; once their visit is cancelled (reassignment) they no longer can.
        Assert.True((await ctx.Quotations().FindCashReceiptForTechnicianAsync(t.H.TechnicianId, t.Quotation.QuotationId, receipt.CashReceiptId, None)).Succeeded);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await ctx.Quotations().FindCashReceiptForTechnicianAsync(stranger.Id, t.Quotation.QuotationId, receipt.CashReceiptId, None)).ErrorCode);
        foreach (var v in await ctx.Db.ServiceVisits.Where(v => v.RequestId == t.RequestId).ToListAsync()) v.Status = VisitStatuses.Cancelled;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await ctx.Quotations().FindCashReceiptForTechnicianAsync(t.H.TechnicianId, t.Quotation.QuotationId, receipt.CashReceiptId, None)).ErrorCode);
        // The Manager can still review it.
        await AcceptedAsync(ctx, t.H.ManagerId, receipt);
    }

    [Fact]
    public async Task UC14_UC15_receipt_numbers_and_photos_are_never_reused_across_payments_installations_included()
    {
        await using var ctx = Create();
        var a = await TechnicianQuotedAsync(ctx);
        var attemptA = await CashOpenedAsync(ctx, a.H.TechnicianId, a.Quotation.QuotationId);
        var photo = await ctx.Installation.VerifiedFileAsync(a.H.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.True((await SendAsync(ctx, a.H.TechnicianId, a.Quotation.QuotationId, attemptA, "PT-SHARED", photo)).Succeeded);

        // A second quotation of the same customer / technician (another fault).
        var fault = await ctx.FaultAsync(a.H, "Second fault");
        var items = await ctx.InspectedAsync(a.H, fault.RequestId, ctx.Clock.GetUtcNow().AddDays(3), Item("Cable"));
        await ctx.DecideAsync(a.H, fault.RequestId, (items[0], ItemCoverages.Chargeable));
        var cable = await CatalogAsync(ctx, "CBL-6MM", "CABLE", 30_000m, "m");
        var qb = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(a.H.TechnicianId, fault.RequestId,
            new TechnicianQuotationInput([Line(items[0], (cable.Id, 10))], null, null), None)).Value!.Quotations);
        var attemptB = await CashOpenedAsync(ctx, a.H.TechnicianId, qb.QuotationId);
        Assert.Equal(PaymentErrorCodes.CashReceiptDuplicate, (await SendAsync(ctx, a.H.TechnicianId, qb.QuotationId, attemptB, "PT-SHARED",
            await ctx.Installation.VerifiedFileAsync(a.H.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.EvidenceReused, (await SendAsync(ctx, a.H.TechnicianId, qb.QuotationId, attemptB, Number(), photo)).ErrorCode);
        // A number used by a direct Manager cash confirmation is taken too.
        var managerEvidence = await ctx.Installation.VerifiedFileAsync(a.H.ManagerId, FilePurposes.PaymentEvidence);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.Payments().ConfirmCashAttemptAsync(a.H.ManagerId, attemptA, new(450_000m, "PT-OTHER", managerEvidence, null), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync(r => r.PaymentAttemptId == attemptB));
    }

    // ------------------------------------------------------------------ 16. rejection and resubmission

    [Fact]
    public async Task UC16_a_rejected_receipt_keeps_the_same_payment_the_technician_resends_and_the_customer_never_pays_again()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var first = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC16");
        Assert.Equal("VALIDATION_FAILED", (await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, first.Revision, " ", None)).ErrorCode);
        var rejected = await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, first.Revision, "Số phiếu không đọc được", None);
        Assert.True(rejected.Succeeded, rejected.Message);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        var view = (await ctx.Quotations().GetPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, None)).Value!;
        Assert.Equal((CashReceiptStatuses.Rejected, "Số phiếu không đọc được"), (view.CurrentCashReceipt!.Status, view.CurrentCashReceipt.ReviewReason));
        Assert.Equal(attemptId, view.Current!.PaymentAttemptId);   // same payment; no new attempt
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, rejected.Value!.Revision, null, None)).ErrorCode);

        var second = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC16");
        await AcceptedAsync(ctx, t.H.ManagerId, second);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.CreatedAt >= t.Quotation.IssuedAt));
        var history = (await ctx.Payments().GetCashReceiptForStaffAsync(second.CashReceiptId, None)).Value!.History;
        Assert.Equal([CashReceiptStatuses.Rejected, CashReceiptStatuses.Accepted], history.Select(r => r.Status));
    }

    // ------------------------------------------------------------------ 19 + 20 + 22. nothing can detach the reported cash while under review

    [Fact]
    public async Task UC19_UC20_UC22_cancel_switch_decline_replace_and_request_cancellation_are_refused_while_a_receipt_is_under_review()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        var q = ctx.Quotations();

        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await q.CancelPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await q.CancelCashAsync(t.H.CustomerId, t.Quotation.QuotationId, None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.CashSelected, (await q.StartPaymentOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId, "ONLINE", None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.CashSelected, (await q.CheckoutAsync(t.H.CustomerId, t.Quotation.QuotationId, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await q.RecordOnSiteDecisionAsync(t.H.TechnicianId, t.Quotation.QuotationId, ServiceOnSiteDecisions.Declined, "changed mind", None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.Customer().CancelAsync(t.H.CustomerId, t.RequestId, "no longer needed", None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.Manager().StopWorkAsync(t.H.ManagerId, t.RequestId, "stop", None)).ErrorCode);
        var managerEvidence = await ctx.Installation.VerifiedFileAsync(t.H.ManagerId, FilePurposes.PaymentEvidence);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.Payments().ConfirmCashAttemptAsync(t.H.ManagerId, attemptId, new(450_000m, Number(), managerEvidence, null), None)).ErrorCode);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Payments().CancelAttemptAsync(attemptId, "direct", None))!.Status);   // service guard
        // Stale review revision.
        Assert.Equal(CashReceiptErrorCodes.Stale, (await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision + 5, null, None)).ErrorCode);

        // Everything is intact; after a rejection the explicit human actions work again (the customer cancels the cash choice).
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.True((await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, "No cash was taken", None)).Succeeded);
        Assert.True((await q.CancelCashAsync(t.H.CustomerId, t.Quotation.QuotationId, None)).Succeeded);
        Assert.Equal(PaymentStatuses.Cancelled, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        Assert.Equal(CashReceiptStatuses.Rejected, (await ctx.Db.CashReceiptSubmissions.AsNoTracking().SingleAsync()).Status);   // evidence history kept
    }

    // ------------------------------------------------------------------ 21. expiry before / after the reported collection

    [Fact]
    public async Task UC21a_cash_taken_before_the_expiry_is_held_through_the_sweep_and_accepted_after_it()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        ctx.Clock.Now = t.Quotation.ExpiresAt.AddHours(3).ToUniversalTime();
        Assert.Equal(0, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);   // held: money already in hand
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        await AcceptedAsync(ctx, t.H.ManagerId, receipt);
        var quotation = await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync();
        Assert.Equal((QuotationStatuses.Paid, (DateTimeOffset?)receipt.CollectedAt), (quotation.Status, quotation.PaidAt));
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, t.RequestId));
    }

    [Fact]
    public async Task UC21b_a_receipt_rejected_after_the_expiry_keeps_the_quotation_for_a_corrected_receipt()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var first = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC21B");
        ctx.Clock.Now = t.Quotation.ExpiresAt.AddHours(1).ToUniversalTime();
        Assert.True((await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, first.Revision, "Blurred", None)).Succeeded);
        Assert.Equal(0, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);   // collected in time: still held
        var second = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC21B", first.CollectedAt);
        await AcceptedAsync(ctx, t.H.ManagerId, second);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task UC21c_cash_reported_as_taken_after_the_expiry_is_recorded_as_unapplied_money_and_never_lost()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        ctx.Clock.Now = t.Quotation.ExpiresAt.AddHours(2).ToUniversalTime();
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, collectedAt: ctx.Clock.GetUtcNow().AddMinutes(-10));
        Assert.True(receipt.CollectedAt > t.Quotation.ExpiresAt);
        var accepted = await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        // The money is recorded (PAID attempt with the receipt), the quotation is NOT paid, the line waits for the Manager's unapplied-money review.
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(PaymentItemStatuses.ReviewRequired, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.PaymentAttemptId == attemptId)).Status);
        var refund = await ctx.Db.RefundRecords.AsNoTracking().SingleAsync(r => r.PaymentAttemptId == attemptId);
        Assert.Equal((RefundStatuses.PendingApproval, 450_000m), (refund.Status, refund.OriginalAmount));
        Assert.Contains(await ctx.Db.PaymentEvents.AsNoTracking().Where(e => e.PaymentAttemptId == attemptId).Select(e => e.EventType).ToListAsync(), e => e == "CASH_RECEIPT_UNAPPLIED");
        Assert.Equal(PaymentItemStatuses.ReviewRequired, Assert.Single((await ctx.Payments().GetCashReceiptForStaffAsync(receipt.CashReceiptId, None)).Value!.Lines!).Status);
        Assert.NotEqual(ServiceRequestStatuses.ReadyForWork, await RequestStatusAsync(ctx, t.RequestId));
        Assert.Single((await ctx.Manager().PageUnappliedMoneyAsync(null, new PageRequest(1, 20), None)).Items, r => r.PaymentAttemptId == attemptId);
    }

    [Fact]
    public async Task UC21d_without_any_receipt_the_sweep_still_withdraws_the_cash_choice_and_expires_the_quotation()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        ctx.Clock.Now = t.Quotation.ExpiresAt.AddMinutes(1).ToUniversalTime();
        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);
        Assert.Equal(PaymentStatuses.Cancelled, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        Assert.Equal(QuotationStatuses.Expired, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await SendAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, Number(),
            await ctx.Installation.VerifiedFileAsync(t.H.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
    }

    // ------------------------------------------------------------------ 22. replacement after review / supersede

    [Fact]
    public async Task UC22_a_new_quotation_version_cannot_replace_the_one_whose_cash_receipt_is_under_review()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        var breaker = await CatalogAsync(ctx, "BRK-NEW", "BREAKER", 100_000m);
        var reissue = await ctx.Quotations().IssueByTechnicianAsync(t.H.TechnicianId, t.RequestId,
            new TechnicianQuotationInput([Line(t.Items[0], (breaker.Id, 1))], null, null), None);
        Assert.False(reissue.Succeeded);
        // The open cash payment already blocks a replacement (existing rule); the receipt guard is the second line of defence.
        Assert.Contains(reissue.ErrorCode, new[] { PaymentErrorCodes.CheckoutInProgress, CashReceiptErrorCodes.UnderReview, AfterSalesErrorCodes.InvalidState });
        Assert.Equal(1, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(CashReceiptStatuses.Submitted, (await ctx.Db.CashReceiptSubmissions.AsNoTracking().SingleAsync(r => r.Id == receipt.CashReceiptId)).Status);
    }

    // ------------------------------------------------------------------ 23. customer isolation

    [Fact]
    public async Task UC23_the_customer_sees_the_payment_status_never_the_internal_receipt_review()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        var customerView = await ctx.Payments().GetAttemptForCustomerAsync(t.H.CustomerId, attemptId, reconcile: false, None);
        Assert.NotNull(customerView);
        Assert.Equal(PaymentStatuses.Pending, customerView!.Status);
        Assert.Null(customerView.EvidenceFileId);
        Assert.Null(customerView.ConfirmedByUserId);
        Assert.Null(await ctx.Payments().GetAttemptForCustomerAsync(Guid.NewGuid(), attemptId, reconcile: false, None));
        await AcceptedAsync(ctx, t.H.ManagerId, receipt);
        customerView = await ctx.Payments().GetAttemptForCustomerAsync(t.H.CustomerId, attemptId, reconcile: false, None);
        Assert.Equal(PaymentStatuses.Paid, customerView!.Status);
        Assert.Null(customerView.EvidenceFileId);   // staff evidence stays private
        var customerRequest = (await ctx.Customer().GetAsync(t.H.CustomerId, t.RequestId, None)).Value!;
        Assert.DoesNotContain("Receipt", System.Text.Json.JsonSerializer.Serialize(customerRequest), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 27. reporting

    [Fact]
    public async Task UC27_an_accepted_after_sales_receipt_is_counted_once_in_the_financial_dashboard()
    {
        await using var ctx = Create();
        var t = await TechnicianQuotedAsync(ctx);
        var attemptId = await CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var first = await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC27");
        Assert.True((await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, first.Revision, "redo", None)).Succeeded);
        await AcceptedAsync(ctx, t.H.ManagerId, await SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-UC27"));
        var period = DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime);
        var view = (await ctx.Dashboard().GetAsync(new DashboardQuery(period.AddDays(-40), period.AddDays(1), null, null, null, null, null), None)).Value!;
        Assert.Equal(450_000m, view.Metrics.Single(m => m.Key == "collected_services").Current);   // two receipts, one payment, counted once
        Assert.Equal(0m, view.Metrics.Single(m => m.Key == "collected_unapplied").Current);
    }
}
