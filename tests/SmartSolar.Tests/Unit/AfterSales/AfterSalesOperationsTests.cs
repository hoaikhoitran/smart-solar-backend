using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence.SystemSettings;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.SystemSettings;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>Shared Sales queue, PayOS settlement edge cases, Admin-configurable timings and the Admin dashboard.</summary>
public sealed class AfterSalesOperationsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<SalesTaskView> QuotationTaskAsync(AfterSalesTestContext ctx, HandedOver h)
    {
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), new DiagnosedItemInput(null, "Cable", "worn", null));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable));
        return Assert.Single(await ctx.Tasks().ListOpenAsync(None));
    }

    [Fact]
    public async Task Sales_tasks_are_claimed_once_and_only_a_manager_reassigns_them_with_a_reason()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var other = await ctx.Installation.Scheduling.SeedSalesAsync("Second Sales");
        var task = await QuotationTaskAsync(ctx, h);
        var tasks = ctx.Tasks();

        Assert.Equal(SalesTaskStatuses.Claimed, (await tasks.ClaimAsync(h.SalesId, task.TaskId, None)).Value!.Status);
        Assert.Equal(AfterSalesErrorCodes.AlreadyClaimed, (await tasks.ClaimAsync(other.Id, task.TaskId, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await tasks.StartAsync(other.Id, task.TaskId, None)).ErrorCode);

        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await tasks.AssignAsync(h.ManagerId, task.TaskId, h.CustomerId, "x", None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await tasks.AssignAsync(h.ManagerId, task.TaskId, other.Id, null, None)).ErrorCode);
        Assert.Equal(other.Id, (await tasks.AssignAsync(h.ManagerId, task.TaskId, other.Id, "First Sales is on leave", None)).Value!.AssignedSalesUserId);
        Assert.Single(await tasks.ListMineAsync(other.Id, None));
        Assert.Empty(await tasks.ListMineAsync(h.SalesId, None));
        // Quotation tasks close by issuing the quotation, not by a follow-up note.
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await tasks.RecordFollowUpAsync(other.Id, task.TaskId, new FollowUpInput(ctx.Clock.GetUtcNow(), "called", null, null), None)).ErrorCode);
    }

    [Fact]
    public async Task A_verified_payment_is_applied_once_and_a_payment_after_the_checkout_deadline_needs_a_manual_refund()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var care = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance);
        var bought = (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, care.Id, null, false), None)).Value!;
        Assert.Equal(ctx.Clock.GetUtcNow().AddMinutes(30), bought.Checkout.ExpiresAt);
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress, (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, care.Id, null, false), None)).ErrorCode);

        // The provider transaction time decides, not the time the webhook arrives.
        var paidAt = ctx.Clock.GetUtcNow().AddMinutes(10);
        ctx.Clock.Now = ctx.Clock.GetUtcNow().AddHours(2);
        await ctx.PayAsync(bought.Checkout, paidAt);
        await ctx.PayAsync(bought.Checkout, paidAt);   // webhook retried
        var term = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == bought.SubscriptionIds[0]);
        Assert.Equal((SubscriptionStatuses.Active, paidAt), (term.Status, term.PaidAt!.Value));
        Assert.Single(await ctx.Db.AfterSalesEvents.AsNoTracking().Where(e => e.SubjectId == term.Id && e.EventType == "PAID").ToListAsync());

        var late = (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null,
            (await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection)).Id, false), None)).Value!;
        await ctx.PayAsync(late.Checkout, ctx.Clock.GetUtcNow().AddMinutes(31));
        Assert.Equal(SubscriptionStatuses.AwaitingPayment, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == late.SubscriptionIds[0])).Status);
        Assert.Equal(PaymentItemStatuses.ReviewRequired, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.PaymentAttemptId == late.Checkout.PaymentAttemptId)).Status);
        var refund = Assert.Single(await ctx.Refunds().ListAsync(RefundStatuses.PendingApproval, None));
        Assert.Equal(late.Checkout.Amount, refund.OriginalAmount);
    }

    [Fact]
    public async Task Timing_settings_changed_by_an_admin_apply_to_new_quotations_only()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var task = await QuotationTaskAsync(ctx, h);
        Assert.True((await ctx.Tasks().ClaimAsync(h.SalesId, task.TaskId, None)).Succeeded);
        var item = (await ctx.Data().ListItemsAsync(task.RequestId!.Value, false, None)).Single().Id;
        var v1 = await ctx.IssueAsync(h, task.RequestId.Value, new QuotationLineInput(item, null, 100_000, 0));

        var settings = new SystemSettingsService(new SystemSettingsUnitOfWork(ctx.Db), SchedulingPolicyDefinitions.Default, ctx.Installation.Scheduling.Policies,
            new SchedulingPolicyCache(TimeSpan.Zero), ctx.Clock, NullLogger<SystemSettingsService>.Instance);
        var updated = await settings.UpdateSchedulingAsync(h.AdminId, new SchedulingSettingsUpdateInput(
            [new SystemSettingChangeInput(AfterSalesSettingKeys.QuotationValidityDays, 3, null)], 0, "Shorter validity"), None);
        Assert.True(updated.Succeeded, updated.Message);
        Assert.Equal(1, await ctx.Db.Set<SystemSettingRevision>().CountAsync(r => r.Key == AfterSalesSettingKeys.QuotationValidityDays));   // audited

        var v2 = await ctx.IssueAsync(h, task.RequestId.Value, new QuotationLineInput(item, null, 90_000, 0));
        Assert.Equal(ctx.Clock.GetUtcNow().AddDays(3), v2.ExpiresAt);
        Assert.Equal(v1.IssuedAt.AddDays(7), (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == v1.QuotationId)).ExpiresAt);
    }

    [Fact]
    public async Task Dashboard_compares_periods_splits_collections_by_category_and_exports_csv()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var care = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, price: 2_000_000m);
        await ctx.ActiveTermAsync(h, care);
        var renewal = await ctx.ActiveTermAsync(h, care);
        Assert.True((await ctx.Packages().RequestCancellationAsync(h.CustomerId, renewal.Id, "Bought twice", None)).Succeeded);
        Assert.True((await ctx.Packages().DecideCancellationAsync(h.ManagerId, renewal.Id, true, null, null, None)).Succeeded);
        var refund = Assert.Single(await ctx.Refunds().ListAsync(RefundStatuses.Approved, None));
        Assert.True((await ctx.Refunds().RecordAsync(h.AdminId, refund.RefundId, ctx.Clock.GetUtcNow(), "TX-9", None)).Succeeded);
        var contractTotal = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId)).TotalAmount;

        var dashboard = ctx.Dashboard();
        var view = (await dashboard.GetAsync(new DashboardQuery(null, null, null, null, null, null, null), None)).Value!;
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            (view.From, view.To, view.CompareFrom, view.CompareTo));
        decimal Current(string key) => view.Metrics.Single(m => m.Key == key).Current;
        Assert.Equal(contractTotal, Current("collected_contracts"));
        Assert.Equal(4_000_000m, Current("collected_packages"));
        Assert.Equal(2_000_000m, Current("refunded"));
        Assert.Equal(contractTotal + 2_000_000m, Current("net_collected"));
        Assert.Equal(1m, Current("completed_installations"));
        Assert.Equal(1m, Current("active_maintenance_subscriptions"));
        Assert.Null(view.Metrics.Single(m => m.Key == "collected_packages").ChangePercent);   // nothing in September

        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await dashboard.GetAsync(new DashboardQuery(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 1), null, null, null, null, null), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await dashboard.GetAsync(new DashboardQuery(null, null, null, null, null, "REPAIR", null), None)).ErrorCode);
        var csv = (await dashboard.ExportCsvAsync(new DashboardQuery(null, null, null, null, null, null, null), None)).Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("metric,description,current (2026-10-01..2026-10-31)", csv[0]);
        Assert.Equal(view.Metrics.Count + 1, csv.Length);
        Assert.Contains(csv, l => l.StartsWith("refunded,") && l.Contains(",2000000,0,"));
    }

    [Fact]
    public async Task Dashboard_zone_filter_keeps_the_zone_s_money_and_excludes_other_zones()
    {
        // Arrange: a contract payment and a package payment for an installation in the Binh Tan zone.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, price: 2_000_000m));
        var zoneId = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == h.ProjectId)).OperationalZoneId;
        var contractTotal = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == h.ContractId)).TotalAmount;

        // Act
        var inZone = (await ctx.Dashboard().GetAsync(new DashboardQuery(null, null, null, null, zoneId, null, null), None)).Value!;
        var otherZone = (await ctx.Dashboard().GetAsync(new DashboardQuery(null, null, null, null, Guid.NewGuid(), null, null), None)).Value!;

        // Assert
        decimal Value(DashboardView view, string key) => view.Metrics.Single(m => m.Key == key).Current;
        Assert.NotNull(zoneId);
        Assert.Equal((contractTotal, 2_000_000m, 1m), (Value(inZone, "collected_contracts"), Value(inZone, "collected_packages"), Value(inZone, "completed_installations")));
        Assert.Equal((0m, 0m, 0m), (Value(otherZone, "collected_contracts"), Value(otherZone, "collected_packages"), Value(otherZone, "completed_installations")));
    }
}
