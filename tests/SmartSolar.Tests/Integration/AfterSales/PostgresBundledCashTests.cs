using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// E2E-09 on a disposable PostgreSQL database (2026-11-18 cash for combined checkouts): concurrent purchases create one checkout; concurrent cash
/// confirmations (two Managers, double clicks) pay the contract once, settle each package line once and create no duplicate entitlement or revenue.
/// </summary>
public sealed class PostgresBundledCashTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task E2E_09_Concurrent_cash_bundle_purchases_and_confirmations_pay_and_settle_everything_exactly_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        var manager = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Manager");
        var care = await ctx.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await ctx.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var customer = ready.Survey.Request.Customer.Id;

        // Five concurrent "buy with cash" clicks: one checkout, one set of orders.
        var purchases = await RaceAsync(database, 5, (db, _) => AfterSalesTestContext.CreateOn(db).Packages(db)
            .PurchaseAsync(customer, new PurchaseInput(contract.ContractId, care.Id, shield.Id, true, PaymentMethods.Cash), None));
        var won = Assert.Single(purchases, p => p.Succeeded);
        Assert.All(purchases.Where(p => !p.Succeeded), p => Assert.Contains(p.ErrorCode, new[] { PaymentErrorCodes.CheckoutInProgress, PaymentErrorCodes.CashSelected }));
        var amount = won.Value!.Checkout.Amount;

        // Six concurrent confirmations, each with its own receipt and evidence.
        var receipts = new List<CashConfirmation>();
        for (var i = 0; i < 6; i++)
            receipts.Add(new(amount, $"PT-RACE-{i}", await ctx.Installation.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence), null));
        var confirmations = await RaceAsync(database, receipts.Count, (db, i) => AfterSalesTestContext.CreateOn(db).Payments(db)
            .ConfirmCashAsync(manager.Id, contract.ContractId, receipts[i], None));

        Assert.Single(confirmations, c => c.Succeeded);
        Assert.All(confirmations.Where(c => !c.Succeeded), c => Assert.Contains(c.ErrorCode,
            new[] { PaymentErrorCodes.AlreadyPaid, PaymentErrorCodes.CashNotSelected }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.ContractId == contract.ContractId));
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync(c => c.Id == contract.ContractId)).Status);
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == contract.ContractId && e.ToStatus == ContractStatuses.Paid));
        var orders = await check.ServiceSubscriptions.AsNoTracking().Where(s => s.ContractId == contract.ContractId).ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, s => Assert.Equal(SubscriptionStatuses.Paid, s.Status));
        var lines = await check.PaymentAttemptItems.AsNoTracking().ToListAsync();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(PaymentItemStatuses.Settled, l.Status));
        Assert.Equal(0, await check.RefundRecords.CountAsync());
        Assert.Equal(2, await check.AfterSalesEvents.CountAsync(e => e.EventType == "PAID"));
    }
}
