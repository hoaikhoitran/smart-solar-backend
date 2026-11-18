using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence.AfterSales;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// DASHBOARD overview (2026-11-18) against a known dataset: every number is checked exactly. Quotation values are offers, not revenue; a pending cash
/// choice is never collected; a duplicate online payment is not collected revenue; a combined checkout is counted once with its package lines split.
/// </summary>
public sealed class AdminOverviewTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static AdminOverview Overview(AfterSalesTestContext ctx) => new(new AdminOverviewQueries(ctx.Db), ctx.Installation.Scheduling.Calendar, ctx.Clock);

    private static OverviewBucket? Bucket(IReadOnlyList<OverviewBucket> list, string key) => list.SingleOrDefault(b => b.Key == key);

    [Fact]
    public async Task DASHBOARD_OVERVIEW_001_Known_dataset_gives_exact_status_counts_collections_and_offers()
    {
        await using var ctx = Create();
        var installation = ctx.Installation;
        var admin = await installation.Scheduling.Inner.SeedUserAsync("Admin");
        var manager = await installation.Scheduling.Inner.SeedUserAsync("Manager");
        var care = await ctx.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await ctx.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var start = installation.Clock.Now;
        var a = await SimplifiedFlowFixture.SignedAsync(installation, "Customer A");
        installation.Clock.Now = start;
        var b = await SimplifiedFlowFixture.SignedAsync(installation, "Customer B");
        installation.Clock.Now = start;
        var c = await SimplifiedFlowFixture.SignedAsync(installation, "Customer C");
        var payments = ctx.Payments();
        async Task<CashConfirmation> Receipt(decimal amount, string reference)
            => new(amount, reference, await installation.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence), null);

        var bundle = (await ctx.Packages().PurchaseAsync(a.Cart.CustomerId, new PurchaseInput(a.Contract.ContractId, care.Id, shield.Id, true, PaymentMethods.Cash), None)).Value!.Checkout;
        Assert.True((await payments.ConfirmCashAttemptAsync(manager.Id, bundle.PaymentAttemptId, await Receipt(bundle.Amount, "PT-A"), None)).Succeeded);
        var online = (await payments.CreateOrGetAttemptAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Value!;
        Assert.True((await payments.CancelCurrentAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Succeeded);
        Assert.True((await payments.SelectCashAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Succeeded);
        Assert.True((await payments.ConfirmCashAsync(manager.Id, b.Contract.ContractId, await Receipt(b.Contract.TotalAmount, "PT-B"), None)).Succeeded);
        await payments.HandleWebhookAsync(installation.PayOs.Webhook(online.OrderCode, (long)online.Amount, "FT-LATE"), None);
        Assert.True((await payments.SelectCashAsync(c.Cart.CustomerId, c.Contract.ContractId, None)).Succeeded);

        var today = installation.Scheduling.Calendar.LocalDateOf(installation.Clock.GetUtcNow());
        var view = (await Overview(ctx).GetAsync(today.AddDays(-40), today.AddDays(1), None)).Value!;

        // Contracts and documents (snapshot).
        Assert.Equal((2, 1), (Bucket(view.ContractsByStatus, ContractStatuses.Paid)!.Count, Bucket(view.ContractsByStatus, ContractStatuses.Signed)!.Count));
        Assert.Equal((3, 3, 0), (view.ContractsWithOfficialPdf, view.ContractsWithSignedScan, view.SignedScansUploadedByStaff));
        Assert.Equal(3, Bucket(view.InstallationQuotationsByStatus, "ACCEPTED")!.Count);
        // Collected money: two confirmed cash payments; the duplicate online payment (REFUND_REQUIRED) is not collected; pending cash is separate.
        var cash = Bucket(view.CollectedByMethod, PaymentMethods.Cash)!;
        Assert.Equal((2, bundle.Amount + b.Contract.TotalAmount), (cash.Count, cash.Amount));
        Assert.Null(Bucket(view.CollectedByMethod, PaymentMethods.Online));
        Assert.Equal((1, c.Contract.TotalAmount), (view.PendingCashChoices.Count, view.PendingCashChoices.Amount));
        Assert.Equal((1, bundle.Amount), (view.CombinedCheckoutsPaid.Count, view.CombinedCheckoutsPaid.Amount));
        Assert.Equal((2, 3_500_000m), (Bucket(view.PackageRevenueBySaleChannel, "WITH_INSTALLATION")!.Count, Bucket(view.PackageRevenueBySaleChannel, "WITH_INSTALLATION")!.Amount));
        Assert.Null(Bucket(view.PackageRevenueBySaleChannel, "SEPARATE"));
        Assert.Equal((2, 0), (Bucket(view.SubscriptionsByKindAndStatus, $"{ServicePackageKinds.Maintenance}:{SubscriptionStatuses.Paid}")!.Count
                              + Bucket(view.SubscriptionsByKindAndStatus, $"{ServicePackageKinds.DisasterProtection}:{SubscriptionStatuses.Paid}")!.Count, view.OpenInstallationSlotOffers));
        // Offers are offers: three issued installation quotations, whatever was paid.
        var offered = await ctx.Db.Quotations.AsNoTracking().Select(q => q.TotalAmount).ToListAsync();
        Assert.Equal((3, offered.Sum()), (view.InstallationQuotationValueIssued.Count, view.InstallationQuotationValueIssued.Amount));
        Assert.Equal((1, (decimal?)b.Contract.TotalAmount), (Bucket(view.RefundRecordsByStatus, RefundStatuses.PendingApproval)!.Count,
            Bucket(view.RefundRecordsByStatus, RefundStatuses.PendingApproval)!.Amount));
        // Attempts created in the period by method and status (reconciles with the rows).
        Assert.Equal(await ctx.Db.PaymentAttempts.CountAsync(), view.PaymentAttemptsCreatedByMethodAndStatus.Sum(x => x.Count));
        Assert.Equal(1, Bucket(view.PaymentAttemptsCreatedByMethodAndStatus, $"{PaymentMethods.Online}:{PaymentStatuses.RefundRequired}")!.Count);
        Assert.NotEmpty(view.Accounts);
    }

    [Fact]
    public async Task DASHBOARD_OVERVIEW_002_Technician_quotations_and_after_sales_counts_appear_by_issuer_and_status()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await TechnicianPricedQuotationTests.DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await TechnicianPricedQuotationTests.CatalogAsync(ctx, "BRK", "BREAKER", 150_000m);
        Assert.True((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([TechnicianPricedQuotationTests.Line(items[0], (breaker.Id, 2))], null, null), None)).Succeeded);

        var view = (await Overview(ctx).GetAsync(null, null, None)).Value!;
        Assert.Equal(1, Bucket(view.ServiceQuotationsByIssuer, QuotationIssuers.Technician)!.Count);
        Assert.Equal(1, Bucket(view.ServiceQuotationsByStatus, QuotationStatuses.Issued)!.Count);
        Assert.Equal(1, Bucket(view.ServiceRequestsByKindAndStatus, $"{ServiceRequestKinds.Fault}:{ServiceRequestStatuses.AwaitingCustomer}")!.Count);
        Assert.Equal((1, 300_000m), (view.ServiceQuotationValueIssued.Count, view.ServiceQuotationValueIssued.Amount));
        // Offered but not collected: no after-sales money in the period.
        Assert.Equal(0, view.CollectedByMethod.Where(x => x.Key == PaymentMethods.Cash).Sum(x => x.Count));
    }

    [Fact]
    public async Task DASHBOARD_OVERVIEW_003_Empty_database_and_invalid_period()
    {
        await using var ctx = Create();
        var view = (await Overview(ctx).GetAsync(null, null, None)).Value!;
        Assert.Empty(view.ContractsByStatus);
        Assert.Empty(view.CollectedByMethod);
        Assert.Equal((0, 0m), (view.PendingCashChoices.Count, view.PendingCashChoices.Amount));
        Assert.Equal((0, 0m), (view.CombinedCheckoutsPaid.Count, view.CombinedCheckoutsPaid.Amount));
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await Overview(ctx).GetAsync(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 1), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await Overview(ctx).GetAsync(new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 1), None)).ErrorCode);
    }
}
