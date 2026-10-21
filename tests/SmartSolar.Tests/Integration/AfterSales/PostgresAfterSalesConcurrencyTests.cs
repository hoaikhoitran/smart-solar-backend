using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// Real PostgreSQL behaviour of Flow 3 on a disposable local database built from the pending schema script (never a shared database):
/// the shared Sales queue claim, disaster-support reservations from one term balance, and the one-term-per-kind exclusion constraint.
/// </summary>
public sealed class PostgresAfterSalesConcurrencyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(PostgresTestDatabase Database, AfterSalesTestContext Ctx)> WorldAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        return (database, AfterSalesTestContext.CreateOn(database.NewContext()));
    }

    [PostgresFact]
    public async Task Racing_sales_users_claim_a_queue_task_exactly_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), new DiagnosedItemInput(null, "Cable", "worn", null));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable));
        var task = Assert.Single(await ctx.Tasks().ListOpenAsync(None));
        var sales = new List<Guid>();
        for (var i = 0; i < 5; i++) sales.Add((await ctx.Installation.Scheduling.SeedSalesAsync($"Racer {i}")).Id);

        var results = await RaceAsync(database, sales.Count, (db, i) => ctx.Tasks(db).ClaimAsync(sales[i], task.TaskId, None));

        var winner = Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(AfterSalesErrorCodes.AlreadyClaimed, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(winner.Value!.AssignedSalesUserId, (await check.SalesTasks.SingleAsync(t => t.Id == task.TaskId)).AssignedSalesUserId);
    }

    [PostgresFact]
    public async Task Parallel_quotations_never_reserve_more_disaster_support_than_the_term_has()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        // 70 %, 5,000,000 per incident, 8,000,000 per term: two 10,000,000 repairs cannot both get 5,000,000.
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));
        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var requests = new List<(Guid Request, Guid Item)>();
        foreach (var (name, day) in new[] { ("Storm broke the east array", 2), ("Storm broke the west array", 3) })
        {
            var storm = await ctx.FaultAsync(h, name, disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddHours(-2));
            Assert.True((await ctx.Manager().DecideClaimAsync(h.ManagerId, storm.RequestId, true, "confirmed", None)).Succeeded);
            var item = (await ctx.InspectedAsync(h, storm.RequestId, ctx.Clock.GetUtcNow().AddDays(day), new DiagnosedItemInput(null, "Panels", "cracked", null)))[0];
            await ctx.DecideAsync(h, storm.RequestId, (item, ItemCoverages.DisasterProtection));
            await ctx.ClaimQuotationTaskAsync(h, storm.RequestId);
            requests.Add((storm.RequestId, item));
        }

        var results = await RaceAsync(database, 2, (db, i) => ctx.Quotations(db).IssueAsync(h.SalesId, requests[i].Request,
            new IssueQuotationInput([new QuotationLineInput(requests[i].Item, null, 9_000_000, 1_000_000)], null, null), None));

        Assert.Contains(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(AfterSalesErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        var stored = await check.ServiceSubscriptions.SingleAsync(s => s.Id == term.Id);
        var reserved = await check.ServiceQuotations.Where(q => q.Status == QuotationStatuses.Issued).SumAsync(q => q.DisasterSupportAmount);
        Assert.Equal(reserved, stored.SupportReserved);
        Assert.True(stored.SupportReserved <= stored.TermLimit, $"reserved {stored.SupportReserved} > term limit {stored.TermLimit}");
    }

    [PostgresFact]
    public async Task The_database_rejects_two_overlapping_terms_of_one_kind()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance));

        await using var db = database.NewContext();
        var copy = await db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id);
        copy.Id = Guid.NewGuid();
        copy.CurrentPaymentAttemptId = null;
        copy.StartsAt = term.StartsAt!.Value.AddMonths(6);   // starts inside the active term
        copy.EndsAt = copy.StartsAt.Value.AddMonths(12);
        copy.Status = SubscriptionStatuses.Paid;
        db.ServiceSubscriptions.Add(copy);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ex_service_subscription_no_overlap", error.InnerException!.Message);
    }

    [PostgresFact]
    public async Task The_database_never_settles_one_package_with_two_payments()
    {
        // Arrange: a package paid and settled through PayOS.
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance));
        await using var db = database.NewContext();
        var settled = await db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.SubscriptionId == term.Id);
        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == settled.PaymentAttemptId);
        Assert.Equal(SmartSolar.Modules.Payments.PaymentItemStatuses.Settled, settled.Status);

        // Act: a second payment line for the same package marked SETTLED.
        attempt.Id = Guid.NewGuid();
        attempt.OrderCode += 1;
        attempt.ProviderReference = null;
        db.PaymentAttempts.Add(attempt);
        db.PaymentAttemptItems.Add(new SmartSolar.Modules.Payments.PaymentAttemptItem
        {
            Id = Guid.NewGuid(), PaymentAttemptId = attempt.Id, ItemType = settled.ItemType, SubscriptionId = term.Id, Amount = settled.Amount,
            Status = SmartSolar.Modules.Payments.PaymentItemStatuses.Settled,
        });

        // Assert
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("ux_payment_attempt_item_settled_subscription", error.InnerException!.Message);
    }
}
