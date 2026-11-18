using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// End-to-end journeys of the 2026-11-18 remediation, in the style of <see cref="InstallationLifecycleE2ETests"/>: the production handlers of every
/// module run in sequence on one database (SQLite); only provider boundaries are fakes (S3 verification, the PayOS server). Survey → technician cart
/// quotation → customer acceptance (contract drafted) → Manager publishes the official PDF → paper signature, scan uploaded by Sales → Manager
/// confirms → technician offers installation times → customer chooses → payment (cash / PayOS, alone or with packages) → schedule confirmed →
/// installation gated by its prerequisites.
/// </summary>
public sealed class CommercialRemediationE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly SlotInterval Slot = new(At(8, 0, Day.AddDays(1)), At(12, 0, Day.AddDays(2)));

    private sealed record Signed(SimplifiedFlowFixture.Cart Cart, Guid ManagerId, Guid SalesId, ContractView Contract);

    /// <summary>Cart → accepted → published → Sales uploads the scan of the paper signed in person → Manager confirms (SIGNED).</summary>
    private static async Task<Signed> PaperSignedAsync(InstallationWorkflowTestContext ctx)
    {
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        // The customer reads the finalized quotation in the portal before accepting.
        var visible = await ctx.Quotations().GetForCustomerAsync(cart.CustomerId, cart.Quotation.QuotationId, None);
        Assert.True(visible.Succeeded, visible.Message);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var sales = await ctx.Scheduling.Inner.SeedUserAsync("Sales");
        var request = await ctx.Db.SurveyRequests.SingleAsync(r => r.Id == cart.RequestId);
        request.AssignedSaleId = sales.Id;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var published = await SimplifiedFlowFixture.PublishedAsync(ctx, manager, await SimplifiedFlowFixture.AcceptAsync(ctx, cart));
        var scan = await ctx.VerifiedFileAsync(sales.Id, FilePurposes.SignedContract, "pdf");
        var uploaded = await ctx.Contracts().SubmitPaperSignedDocumentAsync(sales.Id, false, published.ContractId, scan, published.OfficialDocument!.Sha256!,
            DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime), null, None);
        Assert.Equal(ContractStatuses.UnderReview, uploaded.Value!.Status);
        var signed = await ctx.Contracts().ReviewAsync(manager.Id, true, published.ContractId, uploaded.Value.Revision, ReviewDecision.Approve, null, None);
        Assert.Equal(ContractStatuses.Signed, signed.Value!.Status);
        return new Signed(cart, manager.Id, sales.Id, signed.Value);
    }

    /// <summary>The technician offers one interval and the customer chooses it (PLANNED, not confirmed before payment).</summary>
    private static async Task<InstallationScheduleView> CustomerChoosesAsync(InstallationWorkflowTestContext ctx, Signed s)
    {
        var offer = (await ctx.Schedules().PublishOffersAsync(s.Cart.TechnicianId, s.Contract.ContractId, [Slot], null, None)).Value!.Single();
        var chosen = await ctx.Schedules().SelectOfferAsync(s.Cart.CustomerId, offer.OfferId, None);
        Assert.True(chosen.Succeeded, chosen.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, false), (chosen.Value!.Status, chosen.Value.IsConfirmed));
        return chosen.Value;
    }

    private static async Task AssertConfirmedAndGatedAsync(InstallationWorkflowTestContext ctx, Signed s)
    {
        var confirmed = (await ctx.Schedules().GetForCustomerAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Value!.First();
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (confirmed.Status, confirmed.IsConfirmed));
        var project = await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.ContractId == s.Contract.ContractId);
        Assert.Equal((InstallationStatuses.Scheduled, s.Cart.TechnicianId), (project.Status, project.ResponsibleTechnicianUserId!.Value));
        // Existing prerequisites still gate the start.
        ctx.Clock.Now = Slot.StartsAt.AddMinutes(30).ToUniversalTime();
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, (await ctx.Installations().StartProjectAsync(s.Cart.TechnicianId, project.Id, None)).ErrorCode);
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, project.Id, s.ManagerId);
        Assert.True((await ctx.Installations().StartProjectAsync(s.Cart.TechnicianId, project.Id, None)).Succeeded);
    }

    [Fact]
    public async Task E2E_R01_Installation_with_cash_paper_contract_uploaded_by_sales_and_a_customer_chosen_date()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var s = await PaperSignedAsync(ctx);
        await CustomerChoosesAsync(ctx, s);

        var payments = ctx.PaymentsWithSchedule();
        Assert.True((await payments.SelectCashAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(s.ManagerId, FilePurposes.PaymentEvidence);
        var paid = await payments.ConfirmCashAsync(s.ManagerId, s.Contract.ContractId, new(s.Contract.TotalAmount, "PT-E2E-01", evidence, null), None);
        Assert.True(paid.Succeeded, paid.Message);

        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == s.Contract.ContractId)).Status);
        await AssertConfirmedAndGatedAsync(ctx, s);
    }

    [Fact]
    public async Task E2E_R02_Installation_with_PayOS_is_paid_only_by_the_verified_webhook_never_by_the_browser_return()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var s = await PaperSignedAsync(ctx);
        await CustomerChoosesAsync(ctx, s);
        var payments = ctx.PaymentsWithSchedule();

        var link = (await payments.CreateOrGetAttemptAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Value!;
        Assert.False(string.IsNullOrEmpty(link.CheckoutUrl));
        Assert.False(string.IsNullOrEmpty(link.QrCode));
        // The customer comes back from the PayOS page: the status query asks PayOS, which has no payment yet → still not paid.
        Assert.Equal(ContractPaymentStates.OnlineInProgress, (await payments.GetForCustomerAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Value!.PaymentState);
        // A forged webhook (bad signature) changes nothing.
        Assert.Equal(WebhookOutcomes.Rejected, (await payments.HandleWebhookAsync(
            ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-X", signatureOverride: new string('0', 64)), None)).Outcome);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == s.Contract.ContractId)).Status);

        Assert.Equal(WebhookOutcomes.Applied, (await payments.HandleWebhookAsync(ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-E2E-02"), None)).Outcome);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == s.Contract.ContractId)).Status);
        await AssertConfirmedAndGatedAsync(ctx, s);
    }

    [Fact]
    public async Task E2E_R03_Installation_bundled_with_two_packages_is_one_PayOS_charge_with_item_lines_and_no_double_charge()
    {
        await using var after = AfterSalesTestContext.Create();
        var ctx = after.Installation;
        var admin = await ctx.Scheduling.Inner.SeedUserAsync("Admin");
        var care = await after.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await after.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var s = await PaperSignedAsync(ctx);
        await CustomerChoosesAsync(ctx, s);

        var bought = await after.Packages().PurchaseAsync(s.Cart.CustomerId, new PurchaseInput(s.Contract.ContractId, care.Id, shield.Id, true), None);
        Assert.True(bought.Succeeded, bought.Message);
        var checkout = bought.Value!.Checkout;
        Assert.Equal((s.Contract.TotalAmount + 3_500_000m, 1), (checkout.Amount, ctx.PayOs.CreateCalls));   // one PayOS link for everything
        // A second purchase or a contract-only link meanwhile never opens a second charge.
        Assert.False((await after.Packages().PurchaseAsync(s.Cart.CustomerId, new PurchaseInput(s.Contract.ContractId, care.Id, null, true), None)).Succeeded);
        Assert.Equal(1, ctx.PayOs.CreateCalls);

        var payments = after.PaymentsWithSchedule();
        var webhook = ctx.PayOs.Webhook(checkout.OrderCode, (long)checkout.Amount, "FT-BUNDLE");
        Assert.Equal(WebhookOutcomes.Applied, (await payments.HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await payments.HandleWebhookAsync(webhook, None)).Outcome);

        var lines = await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == checkout.PaymentAttemptId).ToListAsync();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(PaymentItemStatuses.Settled, l.Status));
        Assert.Equal(checkout.Amount, lines.Sum(l => l.Amount));
        Assert.All(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync(), sub => Assert.Equal(SubscriptionStatuses.Paid, sub.Status));
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(0, await ctx.Db.RefundRecords.CountAsync());
        await AssertConfirmedAndGatedAsync(ctx, s);
    }

    [Fact]
    public async Task E2E_R07_A_wrong_amount_and_a_payment_for_another_order_never_mark_the_contract_paid()
    {
        await using var after = AfterSalesTestContext.Create();
        var ctx = after.Installation;
        var s = await PaperSignedAsync(ctx);
        var payments = after.PaymentsWithSchedule();
        var link = (await payments.CreateOrGetAttemptAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Value!;

        // Wrong amount: money recorded for a manual decision, contract unchanged.
        await payments.HandleWebhookAsync(ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount - 1000, "FT-SHORT"), None);
        Assert.Equal(PaymentStatuses.ReviewRequired, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == link.PaymentAttemptId)).Status);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == s.Contract.ContractId)).Status);
        Assert.Equal(1, await ctx.Db.RefundRecords.CountAsync());
        // An unknown order code is acknowledged and ignored.
        Assert.Equal(WebhookOutcomes.Ignored, (await payments.HandleWebhookAsync(ctx.PayOs.Webhook(999_999_999, 1000, "FT-UNKNOWN"), None)).Outcome);
        Assert.Equal(0, await ctx.Db.InstallationProjects.CountAsync());
    }

    [Fact]
    public async Task E2E_R04_Installation_with_two_packages_paid_in_one_cash_payment_confirms_the_customer_chosen_schedule()
    {
        await using var after = AfterSalesTestContext.Create();
        var ctx = after.Installation;
        var admin = await ctx.Scheduling.Inner.SeedUserAsync("Admin");
        var care = await after.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await after.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var s = await PaperSignedAsync(ctx);
        await CustomerChoosesAsync(ctx, s);

        var bought = await after.Packages().PurchaseAsync(s.Cart.CustomerId,
            new PurchaseInput(s.Contract.ContractId, care.Id, shield.Id, true, PaymentMethods.Cash), None);
        Assert.True(bought.Succeeded, bought.Message);
        var due = bought.Value!.Checkout.Amount;
        Assert.Equal(s.Contract.TotalAmount + 3_500_000m, due);
        Assert.Equal(0, ctx.PayOs.CreateCalls);

        var evidence = await ctx.VerifiedFileAsync(s.ManagerId, FilePurposes.PaymentEvidence);
        var paid = await after.PaymentsWithSchedule().ConfirmCashAttemptAsync(s.ManagerId, bought.Value.Checkout.PaymentAttemptId,
            new CashConfirmation(due, "PT-E2E-R04", evidence, null), None);
        Assert.True(paid.Succeeded, paid.Message);

        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == s.Contract.ContractId)).Status);
        Assert.All(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync(), sub => Assert.Equal(SubscriptionStatuses.Paid, sub.Status));
        Assert.All(await ctx.Db.PaymentAttemptItems.AsNoTracking().ToListAsync(), l => Assert.Equal(PaymentItemStatuses.Settled, l.Status));
        await AssertConfirmedAndGatedAsync(ctx, s);
    }
}
