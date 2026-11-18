using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// DASHBOARD / E2E-10 (2026-11-18): with a known dataset the money metrics reconcile exactly with the payment records. Dataset: contract A paid
/// in CASH together with two packages (one combined checkout); contract B paid in cash and then paid AGAIN online (a duplicate that must never be
/// contract revenue); contract C with a cash choice nobody confirmed (pending: never collected). Accepted quotations are never revenue.
/// </summary>
public sealed class DashboardReconciliationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task DASHBOARD_001_Collections_split_applied_money_by_item_and_keep_duplicates_and_pending_cash_out_of_revenue()
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

        // A: one cash payment for the contract and both packages.
        var bundle = (await ctx.Packages().PurchaseAsync(a.Cart.CustomerId,
            new PurchaseInput(a.Contract.ContractId, care.Id, shield.Id, true, PaymentMethods.Cash), None)).Value!.Checkout;
        Assert.True((await payments.ConfirmCashAsync(manager.Id, a.Contract.ContractId, await Receipt(bundle.Amount, "PT-A"), None)).Succeeded);

        // B: cash confirmed, then the old online link is paid too (duplicate, REFUND_REQUIRED), and its webhook delivered twice.
        var online = (await payments.CreateOrGetAttemptAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Value!;
        Assert.True((await payments.CancelCurrentAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Succeeded);
        Assert.True((await payments.SelectCashAsync(b.Cart.CustomerId, b.Contract.ContractId, None)).Succeeded);
        Assert.True((await payments.ConfirmCashAsync(manager.Id, b.Contract.ContractId, await Receipt(b.Contract.TotalAmount, "PT-B"), None)).Succeeded);
        var webhook = installation.PayOs.Webhook(online.OrderCode, (long)online.Amount, "FT-LATE");
        await payments.HandleWebhookAsync(webhook, None);
        await payments.HandleWebhookAsync(webhook, None);
        Assert.Equal(PaymentStatuses.RefundRequired, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(x => x.Id == online.PaymentAttemptId)).Status);

        // C: cash chosen, never confirmed.
        Assert.True((await payments.SelectCashAsync(c.Cart.CustomerId, c.Contract.ContractId, None)).Succeeded);

        var period = DateOnly.FromDateTime(installation.Clock.GetUtcNow().UtcDateTime);
        var view = (await ctx.Dashboard().GetAsync(new DashboardQuery(period.AddDays(-40), period.AddDays(1), null, null, null, null, null), None)).Value!;
        decimal Metric(string key) => view.Metrics.Single(m => m.Key == key).Current;

        var contractRevenue = a.Contract.TotalAmount + b.Contract.TotalAmount;   // each contract once; C is pending
        Assert.Equal(contractRevenue, Metric("collected_contracts"));
        Assert.Equal(3_500_000m, Metric("collected_packages"));                    // bundle lines, not the whole bundle
        Assert.Equal(0m, Metric("collected_services"));
        Assert.Equal(b.Contract.TotalAmount, Metric("collected_unapplied"));       // the duplicate, once despite two deliveries
        Assert.Equal(contractRevenue + 3_500_000m + b.Contract.TotalAmount, Metric("collected_total"));
        Assert.Equal(Metric("collected_total"), Metric("net_collected"));          // nothing actually refunded
        Assert.Equal(2m, Metric("package_purchases"));

        // Reconciliation with the detail records: gross = every amount actually received in the period.
        var received = await ctx.Db.PaymentAttempts.AsNoTracking()
            .Where(x => x.AmountPaid != null && (x.Status == PaymentStatuses.Paid || x.Status == PaymentStatuses.RefundRequired || x.Status == PaymentStatuses.ReviewRequired))
            .Select(x => x.AmountPaid!.Value).ToListAsync();
        Assert.Equal(received.Sum(v => (decimal)v), Metric("collected_total"));

        // The same duplicate is listed once on the read-only unapplied-money ledger (reconciliation; nothing is resolved there).
        var ledger = await ctx.Manager().PageUnappliedMoneyAsync(null, new SmartSolar.Modules.Common.Paging.PageRequest(1, 20), None);
        var row = Assert.Single(ledger.Items);
        Assert.Equal((RefundStatuses.PendingApproval, b.Contract.TotalAmount, (Guid?)online.PaymentAttemptId), (row.Status, row.Amount, row.PaymentAttemptId));
        Assert.Equal(Metric("collected_unapplied"), ledger.Items.Sum(r => r.Amount));
        Assert.Equal(0, (await ctx.Manager().PageUnappliedMoneyAsync(RefundStatuses.Refunded, new SmartSolar.Modules.Common.Paging.PageRequest(1, 20), None)).TotalItems);
    }

    [Fact]
    public async Task DASHBOARD_002_Zero_data_and_date_boundaries()
    {
        await using var ctx = Create();
        var empty = (await ctx.Dashboard().GetAsync(new DashboardQuery(null, null, null, null, null, null, null), None)).Value!;
        Assert.All(empty.Metrics, m => Assert.Equal(0m, m.Current));
        Assert.All(empty.Metrics, m => Assert.Null(m.ChangePercent));

        // Money confirmed today is inside a period ending today and outside a period ending yesterday ([from, to] local dates).
        var installation = ctx.Installation;
        var manager = await installation.Scheduling.Inner.SeedUserAsync("Manager");
        var signed = await SimplifiedFlowFixture.SignedAsync(installation);
        var payments = ctx.Payments();
        Assert.True((await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Succeeded);
        var evidence = await installation.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence);
        Assert.True((await payments.ConfirmCashAsync(manager.Id, signed.Contract.ContractId, new(signed.Contract.TotalAmount, "PT-1", evidence, null), None)).Succeeded);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(installation.Clock.GetUtcNow(), installation.Scheduling.Calendar.TimeZone).DateTime);

        decimal Collected(DateOnly from, DateOnly to)
            => ctx.Dashboard().GetAsync(new DashboardQuery(from, to, null, null, null, null, null), None).Result.Value!.Metrics.Single(m => m.Key == "collected_contracts").Current;
        Assert.Equal(signed.Contract.TotalAmount, Collected(today, today));
        Assert.Equal(0m, Collected(today.AddDays(-5), today.AddDays(-1)));
        Assert.Equal(0m, Collected(today.AddDays(1), today.AddDays(3)));
    }
}
