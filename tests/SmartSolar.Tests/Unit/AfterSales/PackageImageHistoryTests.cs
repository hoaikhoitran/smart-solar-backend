using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>PKG-IMG-008: changing or removing a package's cover never touches what customers already bought (the purchase snapshot).</summary>
public sealed class PackageImageHistoryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task PKG_IMG_008_Changing_and_removing_the_cover_leaves_purchased_orders_unchanged()
    {
        await using var ctx = Create();
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        var created = await ctx.Packages().CreatePackageAsync(admin.Id, new PackageInput(ServicePackageKinds.Maintenance, "Care 12", "Two visits", 2_000_000m, 12, [6, 12],
            null, null, null, null, true, "https://cdn.example.com/first.jpg"), None);
        Assert.True(created.Succeeded, created.Message);
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var customer = ready.Survey.Request.Customer.Id;
        var bought = await ctx.Packages().PurchaseAsync(customer, new PurchaseInput(contract.ContractId, created.Value!.PackageId, null, true), None);
        Assert.True(bought.Succeeded, bought.Message);
        var before = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync();

        var replaced = await ctx.Packages().UpdatePackageAsync(admin.Id, created.Value.PackageId, new PackageInput(ServicePackageKinds.Maintenance, "Care 12", "Two visits",
            2_000_000m, 12, [6, 12], null, null, null, null, true, "https://cdn.example.com/second.jpg"), created.Value.Revision, None);
        Assert.True(replaced.Succeeded, replaced.Message);
        var removed = await ctx.Packages().UpdatePackageAsync(admin.Id, created.Value.PackageId, new PackageInput(ServicePackageKinds.Maintenance, "Care 12", "Two visits",
            2_000_000m, 12, [6, 12], null, null, null, null, true), replaced.Value!.Revision, None);
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Null(removed.Value!.ImageUrl);

        var after = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync();
        Assert.Equal((before.PackageName, before.Price, before.DurationMonths, before.VisitMonths, before.Status, before.Revision, before.UpdatedAt),
            (after.PackageName, after.Price, after.DurationMonths, after.VisitMonths, after.Status, after.Revision, after.UpdatedAt));
        Assert.Equal(before.CurrentPaymentAttemptId, after.CurrentPaymentAttemptId);
        var lines = await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.SubscriptionId == after.Id).ToListAsync();
        Assert.Equal(2_000_000m, Assert.Single(lines).Amount);
    }
}
