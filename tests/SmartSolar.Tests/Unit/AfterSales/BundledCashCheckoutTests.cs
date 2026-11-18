using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// INSTALL-BUNDLE + INSTALL-CASH (2026-11-18): the customer pays the installation contract and the chosen packages together in CASH. One CASH
/// attempt carries one line per item (contract total unchanged), the Manager/Admin confirms the exact total once, and that single transaction
/// pays the contract and settles every package line (price snapshot kept). Online and cash never run together; a withdrawn cash choice releases
/// the orders; a pending cash order is never expired by the online checkout deadline; nothing is paid or activated twice.
/// </summary>
public sealed class BundledCashCheckoutTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record World(AfterSalesTestContext Ctx, Guid Customer, Guid ManagerId, ContractView Contract, ServicePackage Care, ServicePackage Shield);

    private static async Task<World> SignedWithPackagesAsync(AfterSalesTestContext ctx)
    {
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        var manager = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Manager");
        var care = await ctx.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await ctx.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        return new World(ctx, ready.Survey.Request.Customer.Id, manager.Id, contract, care, shield);
    }

    private static PurchaseInput Cash(World w, bool care = true, bool shield = true)
        => new(w.Contract.ContractId, care ? w.Care.Id : null, shield ? w.Shield.Id : null, true, PaymentMethods.Cash);

    private static async Task<CashConfirmation> ReceiptAsync(World w, decimal amount, string reference = "PT-0001")
        => new(amount, reference, await w.Ctx.Installation.VerifiedFileAsync(w.ManagerId, FilePurposes.PaymentEvidence), "Thu tại văn phòng");

    [Fact]
    public async Task INSTALL_BUNDLE_CASH_001_One_cash_payment_pays_the_contract_and_settles_each_package_line_once()
    {
        await using var ctx = Create();
        var w = await SignedWithPackagesAsync(ctx);

        var bought = await ctx.Packages().PurchaseAsync(w.Customer, Cash(w), None);
        Assert.True(bought.Succeeded, bought.Message);
        var checkout = bought.Value!.Checkout;
        Assert.Equal((PaymentMethods.Cash, PaymentStatuses.Pending, w.Contract.TotalAmount + 3_500_000m, (DateTimeOffset?)null, (string?)null),
            (checkout.Method, checkout.Status, checkout.Amount, checkout.ExpiresAt, checkout.CheckoutUrl));
        var lines = await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == checkout.PaymentAttemptId).ToListAsync();
        Assert.Equal(new[] { PaymentItemTypes.Contract, PaymentItemTypes.DisasterPackage, PaymentItemTypes.MaintenancePackage }, lines.Select(l => l.ItemType).Order());
        Assert.Equal(w.Contract.TotalAmount, lines.Single(l => l.ItemType == PaymentItemTypes.Contract).Amount);
        Assert.Equal(checkout.Amount, lines.Sum(l => l.Amount));
        Assert.Equal(0, ctx.Installation.PayOs.CreateCalls);   // no PayOS link for a cash choice
        var state = (await ctx.Payments().GetForCustomerAsync(w.Customer, w.Contract.ContractId, None)).Value!;
        Assert.Equal((ContractPaymentStates.AwaitingCashConfirmation, ContractStatuses.Signed), (state.PaymentState, state.ContractStatus));

        // Only the exact total of all lines is accepted (the contract total alone is a partial payment).
        Assert.Equal(PaymentErrorCodes.CashAmountMismatch,
            (await ctx.Payments().ConfirmCashAsync(w.ManagerId, w.Contract.ContractId, await ReceiptAsync(w, w.Contract.TotalAmount), None)).ErrorCode);

        var paid = await ctx.Payments().ConfirmCashAsync(w.ManagerId, w.Contract.ContractId, await ReceiptAsync(w, checkout.Amount), None);
        Assert.True(paid.Succeeded, paid.Message);
        Assert.Equal((PaymentStatuses.Paid, checkout.Amount, w.ManagerId), (paid.Value!.Status, paid.Value.AmountPaid!.Value, paid.Value.ConfirmedByUserId!.Value));
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == w.Contract.ContractId)).Status);
        var settled = await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == checkout.PaymentAttemptId).ToListAsync();
        Assert.All(settled, l => Assert.Equal(PaymentItemStatuses.Settled, l.Status));
        var orders = await ctx.Db.ServiceSubscriptions.AsNoTracking().Where(s => s.ContractId == w.Contract.ContractId).ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.All(orders, s => Assert.Equal((SubscriptionStatuses.Paid, (DateTimeOffset?)null), (s.Status, s.StartsAt)));   // term starts at handover
        Assert.Equal(2_000_000m, orders.Single(s => s.Kind == ServicePackageKinds.Maintenance).Price);
        Assert.Equal(0, await ctx.Db.RefundRecords.CountAsync());

        // A second confirmation (double click, second Manager) is refused: nothing is paid or activated twice.
        Assert.Equal(PaymentErrorCodes.AlreadyPaid,
            (await ctx.Payments().ConfirmCashAsync(w.ManagerId, w.Contract.ContractId, await ReceiptAsync(w, checkout.Amount, "PT-0002"), None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(2, await ctx.Db.ServiceSubscriptions.CountAsync(s => s.Status == SubscriptionStatuses.Paid));
    }

    [Fact]
    public async Task INSTALL_BUNDLE_CASH_002_Cash_needs_a_known_method_active_packages_and_follows_the_contract_rule()
    {
        await using var ctx = Create();
        var w = await SignedWithPackagesAsync(ctx);
        var packages = ctx.Packages();

        // 2026-11-18 (second pass): cash is no longer restricted to bundles. Packages alone on a SIGNED, unpaid contract follow the existing rule for
        // every method — pay the installation first or together (old expectation: VALIDATION_FAILED "cash only with the contract").
        Assert.Equal(AfterSalesErrorCodes.InvalidState,
            (await packages.PurchaseAsync(w.Customer, new PurchaseInput(w.Contract.ContractId, w.Care.Id, null, false, PaymentMethods.Cash), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed,
            (await packages.PurchaseAsync(w.Customer, new PurchaseInput(w.Contract.ContractId, w.Care.Id, null, true, "CHEQUE"), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed,
            (await packages.PurchaseAsync(w.Customer, new PurchaseInput(w.Contract.ContractId, null, null, true, PaymentMethods.Cash), None)).ErrorCode);
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin 2");
        Assert.True((await packages.UpdatePackageAsync(admin.Id, w.Care.Id,
            new PackageInput(w.Care.Kind, w.Care.Name, null, w.Care.Price, 12, [6, 12], null, null, null, null, false), w.Care.Revision, None)).Succeeded);
        Assert.Equal(AfterSalesErrorCodes.PackageInactive, (await packages.PurchaseAsync(w.Customer, Cash(w, shield: false), None)).ErrorCode);
        // Another customer's contract is not found.
        Assert.Equal(AfterSalesErrorCodes.ProjectNotFound, (await packages.PurchaseAsync(Guid.NewGuid(), Cash(w, care: false), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.ServiceSubscriptions.CountAsync());
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync());
    }

    [Fact]
    public async Task INSTALL_BUNDLE_CASH_003_Online_and_cash_never_run_together_and_a_second_checkout_creates_nothing()
    {
        await using var ctx = Create();
        var w = await SignedWithPackagesAsync(ctx);

        // A contract-only cash choice must be withdrawn before packages are added to the cash payment.
        Assert.True((await ctx.Payments().SelectCashAsync(w.Customer, w.Contract.ContractId, None)).Succeeded);
        Assert.Equal(PaymentErrorCodes.CashSelected, (await ctx.Packages().PurchaseAsync(w.Customer, Cash(w), None)).ErrorCode);
        Assert.True((await ctx.Payments().CancelCurrentAsync(w.Customer, w.Contract.ContractId, None)).Succeeded);

        // An online combined checkout in progress blocks the cash one (and the reverse).
        var online = await ctx.Packages().PurchaseAsync(w.Customer, new PurchaseInput(w.Contract.ContractId, null, w.Shield.Id, true), None);
        Assert.True(online.Succeeded, online.Message);
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress, (await ctx.Packages().PurchaseAsync(w.Customer, Cash(w, shield: false), None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.ServiceSubscriptions.CountAsync());
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Pending));
    }

    [Fact]
    public async Task INSTALL_BUNDLE_CASH_004_A_pending_cash_order_is_not_expired_by_the_online_deadline_and_a_withdrawn_one_is_released()
    {
        await using var ctx = Create();
        var w = await SignedWithPackagesAsync(ctx);
        var checkout = (await ctx.Packages().PurchaseAsync(w.Customer, Cash(w), None)).Value!.Checkout;

        // Two days later (far past the online checkout window) the sweep leaves the cash order alone, and the receipt still settles it.
        ctx.Clock.Now = ctx.Clock.Now.AddDays(2);
        Assert.Equal(0, (await ctx.Sweep().RunOnceAsync(None)).ExpiredCheckouts);
        Assert.All(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync(), s => Assert.Equal(SubscriptionStatuses.AwaitingPayment, s.Status));

        // Withdrawn by the customer: the attempt is CANCELLED and the sweep releases the orders (no unpaid order stays blocking).
        Assert.Equal(PaymentStatuses.Cancelled, (await ctx.Payments().CancelCurrentAsync(w.Customer, w.Contract.ContractId, None)).Value!.Status);
        Assert.Equal(2, (await ctx.Sweep().RunOnceAsync(None)).ExpiredCheckouts);
        Assert.All(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync(), s => Assert.Equal(SubscriptionStatuses.CheckoutExpired, s.Status));
        // Confirming a withdrawn cash choice is impossible.
        Assert.Equal(PaymentErrorCodes.CashNotSelected,
            (await ctx.Payments().ConfirmCashAsync(w.ManagerId, w.Contract.ContractId, await ReceiptAsync(w, checkout.Amount), None)).ErrorCode);

        // The customer can buy again (cash), and that one settles normally.
        var again = await ctx.Packages().PurchaseAsync(w.Customer, Cash(w), None);
        Assert.True(again.Succeeded, again.Message);
        Assert.True((await ctx.Payments().ConfirmCashAsync(w.ManagerId, w.Contract.ContractId, await ReceiptAsync(w, again.Value!.Checkout.Amount, "PT-0009"), None)).Succeeded);
        Assert.Equal(2, await ctx.Db.ServiceSubscriptions.CountAsync(s => s.Status == SubscriptionStatuses.Paid));
        Assert.Equal(2, await ctx.Db.ServiceSubscriptions.CountAsync(s => s.Status == SubscriptionStatuses.CheckoutExpired));
    }
}
