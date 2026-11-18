using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// AS-ONSITE (2026-11-18): after-sales quotations made on site. The assigned technician prices the chargeable items, shows the quotation on their
/// device, records the customer's AGREED / DECLINED answer with their own account and opens the payment (PayOS QR or cash confirmed by a Manager);
/// paid work starts only after the payment is verified; the technician records the customer's confirmation of the result. The customer never has
/// to open the app: nothing asks for an approval, and the in-app accept / reject of a technician quotation is refused.
/// </summary>
public sealed class OnSiteServiceQuotationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(HandedOver H, Guid RequestId, IReadOnlyList<Guid> Items, ServiceQuotationView Quotation)> IssuedOnSiteAsync(AfterSalesTestContext ctx)
    {
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var labor = await CatalogAsync(ctx, "SVC-REPAIR", ProductTypes.OtherService, 300_000m, "JOB");
        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 1), (labor.Id, 1))], null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        return (h, requestId, items, Assert.Single(issued.Value!.Quotations));
    }

    [Fact]
    public async Task AS_ONSITE_001_The_technician_records_the_agreement_and_opens_the_QR_and_the_customer_is_never_asked()
    {
        await using var ctx = Create();
        var (h, requestId, _, q) = await IssuedOnSiteAsync(ctx);
        // Nothing asks the customer to review a technician quotation; the in-app decision is refused.
        Assert.Equal(0, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.RecipientUserId == h.CustomerId && n.Type == "QUOTATION_ISSUED"));
        Assert.Equal(AfterSalesErrorCodes.DecidedOnSite, (await ctx.Quotations().AcceptAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.DecidedOnSite, (await ctx.Quotations().RejectAsync(h.CustomerId, q.QuotationId, "no", None)).ErrorCode);

        var agreed = await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, "agreed in front of the inverter", None);
        Assert.True(agreed.Succeeded, agreed.Message);
        var history = await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.SubjectId == requestId && e.EventType == "QUOTATION_AGREED_ON_SITE");
        Assert.Equal((Guid?)h.TechnicianId, history.ActorUserId);   // the technician's own account

        var link = await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, "ONLINE", None);
        Assert.True(link.Succeeded, link.Message);
        Assert.Equal((PaymentMethods.Online, 450_000m), (link.Value!.Method, link.Value.Amount));
        Assert.Equal(PaymentSources.Technician, (await ctx.Db.PaymentEvents.AsNoTracking().SingleAsync(e => e.PaymentAttemptId == link.Value.PaymentAttemptId && e.EventType == "CREATED")).Source);
        // Before the money is verified no paid work may start.
        Assert.NotEqual(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);

        var webhook = ctx.Installation.PayOs.Webhook(link.Value.OrderCode, (long)link.Value.Amount, "FT-AS-ONSITE");
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        var state = (await ctx.Quotations().GetPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, None)).Value!;
        Assert.Equal((QuotationStatuses.Paid, PaymentStatuses.Paid), (state.QuotationStatus, state.Current!.Status));
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, "CASH", None)).ErrorCode);
    }

    [Fact]
    public async Task AS_ONSITE_002_A_decline_recorded_on_site_charges_nothing_and_only_the_assigned_technician_records_it()
    {
        await using var ctx = Create();
        var (h, requestId, items, q) = await IssuedOnSiteAsync(ctx);
        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);

        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, "MAYBE", null, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, "DECLINED", " ", None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await ctx.Quotations().RecordOnSiteDecisionAsync(stranger.Id, q.QuotationId, "AGREED", null, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await ctx.Quotations().StartPaymentOnSiteAsync(stranger.Id, q.QuotationId, "CASH", None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, "CASH", None)).ErrorCode);   // not agreed

        var declined = await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, "DECLINED", "Too expensive for now", None);
        Assert.True(declined.Succeeded, declined.Message);
        Assert.Equal(QuotationStatuses.Rejected, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.All(await ctx.Db.ServiceItems.AsNoTracking().Where(i => items.Contains(i.Id)).ToListAsync(), i => Assert.Equal(ItemStatuses.Declined, i.Status));
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
        Assert.Single(await ctx.Db.AfterSalesEvents.AsNoTracking().Where(e => e.SubjectId == requestId && e.EventType == "QUOTATION_DECLINED_ON_SITE").ToListAsync());
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, "AGREED", null, None)).ErrorCode);
    }

    [Fact]
    public async Task AS_ONSITE_003_Cash_taken_on_site_waits_for_the_manager_and_paid_work_starts_only_afterwards()
    {
        await using var ctx = Create();
        var (h, requestId, items, q) = await IssuedOnSiteAsync(ctx);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, "AGREED", null, None)).Succeeded);
        var cash = await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, "CASH", None);
        Assert.True(cash.Succeeded, cash.Message);
        Assert.Equal((PaymentMethods.Cash, PaymentStatuses.Pending), (cash.Value!.Method, cash.Value.Status));
        Assert.Equal(cash.Value.PaymentAttemptId, (await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, "CASH", None)).Value!.PaymentAttemptId);
        var cashChoice = await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.SubjectId == requestId && e.EventType == "CASH_SELECTED");
        Assert.Equal((Guid?)h.TechnicianId, cashChoice.ActorUserId);
        // No repair visit before the money is confirmed.
        var revision = (await ctx.Manager().GetAsync(requestId, None)).Value!.Revision;
        Assert.False((await ctx.Manager().ScheduleVisitAsync(h.ManagerId, requestId, new ScheduleVisitInput(h.TechnicianId, Later(5), null, null, revision), None)).Succeeded);

        var evidence = await ctx.Installation.VerifiedFileAsync(h.ManagerId, FilePurposes.PaymentEvidence);
        Assert.True((await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, cash.Value.PaymentAttemptId, new(450_000m, "PT-AS-ONSITE", evidence, null), None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);

        // Switching method once paid is impossible; the repair and the on-site confirmation close the request without the customer's app.
        Assert.Equal(PaymentErrorCodes.AttemptNotFound, (await ctx.Quotations().CancelPaymentOnSiteAsync(h.TechnicianId, q.QuotationId, None)).ErrorCode);
        var repair = await ctx.VisitStartedAsync(h, requestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Repaired", [new ItemResultInput(items[0], "repaired", null)], [], null, None)).Succeeded);
        var closed = await ctx.Customer().ConfirmCompletionOnSiteAsync(h.TechnicianId, requestId, "Customer checked the inverter output with me", null, None);
        Assert.True(closed.Succeeded, closed.Message);
        Assert.Equal(ServiceRequestStatuses.Completed, closed.Value!.Status);
    }

    [Fact]
    public async Task AS_ONSITE_004_Same_visit_paid_repair_continues_only_after_the_verified_payment()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var visit = await ctx.VisitStartedAsync(h, request.RequestId, Later(3, 9), endsAt: Later(3, 12));
        Assert.True((await ctx.Technician().ReportInspectionAsync(h.TechnicianId, visit, "Breaker worn out", false,
            [new DiagnosedItemInput(null, "AC breaker", "contacts burnt", "C16 breaker")], stayOnSite: true, None)).Value!.WaitingOnSite);
        var item = (await ctx.Data().ListItemsAsync(request.RequestId, false, None)).Single().Id;
        await ctx.DecideAsync(h, request.RequestId, (item, ItemCoverages.Chargeable));
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var quotation = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, request.RequestId,
            new TechnicianQuotationInput([Line(item, (breaker.Id, 1))], null, null), None)).Value!.Quotations);

        Assert.Equal(AfterSalesErrorCodes.PaymentRequired, (await ctx.Technician().ContinueWithRepairAsync(h.TechnicianId, visit, 60, true, None)).ErrorCode);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, quotation.QuotationId, "AGREED", null, None)).Succeeded);
        Assert.Equal(AfterSalesErrorCodes.PaymentRequired, (await ctx.Technician().ContinueWithRepairAsync(h.TechnicianId, visit, 60, true, None)).ErrorCode);
        var link = (await ctx.Quotations().StartPaymentOnSiteAsync(h.TechnicianId, quotation.QuotationId, "ONLINE", None)).Value!;
        await ctx.PayAsync(link);

        var continued = await ctx.Technician().ContinueWithRepairAsync(h.TechnicianId, visit, 60, partsAvailable: true, None);
        Assert.True(continued.Succeeded, continued.Message);
        Assert.Equal(VisitPurposes.Repair, continued.Value!.Purpose);
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, visit, "Breaker replaced", [new ItemResultInput(item, "replaced", "C16 breaker")], [], null, None)).Succeeded);
        Assert.True((await ctx.Customer().ConfirmCompletionOnSiteAsync(h.TechnicianId, request.RequestId, "Tested together with the customer", null, None)).Succeeded);
        var view = (await ctx.Customer().GetAsync(h.CustomerId, request.RequestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.Completed, view.Status);
        Assert.Single(view.Visits);   // inspection, payment and repair in one visit
    }

    [Fact]
    public async Task AS_ONSITE_005_Free_warranty_work_needs_no_quotation_and_no_payment_and_the_technician_records_the_confirmation()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.InstallationWarranty);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        var repair = await ctx.VisitStartedAsync(h, requestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Replaced under warranty", [new ItemResultInput(items[0], "replaced", null)], [], null, None)).Succeeded);

        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Customer().ConfirmCompletionOnSiteAsync(stranger.Id, requestId, "x", null, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await ctx.Customer().ConfirmCompletionOnSiteAsync(h.TechnicianId, requestId, " ", null, None)).ErrorCode);
        var closed = await ctx.Customer().ConfirmCompletionOnSiteAsync(h.TechnicianId, requestId, "Customer saw the system back online", null, None);
        Assert.True(closed.Succeeded, closed.Message);
        Assert.Equal(ServiceRequestStatuses.Completed, closed.Value!.Status);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Customer().ConfirmCompletionOnSiteAsync(h.TechnicianId, requestId, "again", null, None)).ErrorCode);
        var history = await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.SubjectId == requestId && e.EventType == "COMPLETION_CONFIRMED_ON_SITE");
        Assert.Equal((Guid?)h.TechnicianId, history.ActorUserId);
    }
}
