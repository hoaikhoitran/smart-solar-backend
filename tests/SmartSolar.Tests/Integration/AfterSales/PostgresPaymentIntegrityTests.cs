using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// Real PostgreSQL backstops for payments on a disposable local database: a contract is paid once whichever checkout path the
/// customer used, a CONTRACT line always names its checkout's contract (once), and stored checkout / request references exist.
/// </summary>
public sealed class PostgresPaymentIntegrityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(PostgresTestDatabase Database, AfterSalesTestContext Ctx)> WorldAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        return (database, AfterSalesTestContext.CreateOn(database.NewContext()));
    }

    private static async Task<PostgresException> SqlFailsAsync(PostgresTestDatabase database, FormattableString sql)
    {
        await using var db = database.NewContext();
        return await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));
    }

    [PostgresFact]
    public async Task A_contract_is_paid_once_when_a_contract_only_link_and_a_combined_checkout_are_both_paid()
    {
        // Arrange: a contract-only (legacy) link, cancelled but still open in a browser tab, and then a combined contract + package checkout.
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        var care = await ctx.PackageAsync(admin.Id, ServicePackageKinds.Maintenance);
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var customer = ready.Survey.Request.Customer.Id;
        var legacy = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        var whileLive = await ctx.Packages().PurchaseAsync(customer, new PurchaseInput(contract.ContractId, care.Id, null, true), None);
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress, whileLive.ErrorCode);   // one payable checkout per contract
        Assert.True((await ctx.Payments().CancelCurrentAsync(customer, contract.ContractId, None)).Succeeded);
        var combined = (await ctx.Packages().PurchaseAsync(customer, new PurchaseInput(contract.ContractId, care.Id, null, true), None)).Value!.Checkout;

        // Act: the customer completes both; the two PayOS webhooks race.
        var webhooks = new[]
        {
            ctx.Installation.PayOs.Webhook(legacy.OrderCode, (long)legacy.Amount, "FT-LEGACY"),
            ctx.Installation.PayOs.Webhook(combined.OrderCode, (long)combined.Amount, "FT-COMBINED"),
        };
        await RaceAsync(database, 2, (db, i) => ctx.Payments(db).HandleWebhookAsync(webhooks[i], None));

        // Assert: one PAID checkout, one transition to PAID, the other money flagged for a refund; packages follow their checkout.
        await using var check = database.NewContext();
        var attempts = await check.PaymentAttempts.AsNoTracking().Where(a => a.ContractId == contract.ContractId).ToListAsync();
        var paid = Assert.Single(attempts, a => a.Status == PaymentStatuses.Paid);
        Assert.Single(attempts, a => a.Status == PaymentStatuses.RefundRequired);
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync(c => c.Id == contract.ContractId)).Status);
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == contract.ContractId && e.ToStatus == ContractStatuses.Paid));
        var combinedWon = paid.Id == combined.PaymentAttemptId;
        var contractLine = await check.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.ContractId == contract.ContractId);
        Assert.Equal(combinedWon ? PaymentItemStatuses.Settled : PaymentItemStatuses.Pending, contractLine.Status);
        var package = await check.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.ContractId == contract.ContractId);
        Assert.Equal(combinedWon, package.Status == SubscriptionStatuses.Paid);
    }

    [PostgresFact]
    public async Task The_database_rejects_contract_lines_that_repeat_or_name_another_checkout()
    {
        // Arrange: the contract was paid by a contract-only checkout; a package was then bought in a service-only checkout.
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance));
        var contractCheckout = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == h.ContractId);
        var serviceCheckout = term.CurrentPaymentAttemptId!.Value;

        // Act / Assert: a CONTRACT line inside a checkout that is not for that contract (here: service-only).
        var foreign = await SqlFailsAsync(database, $"""
            INSERT INTO payment_attempt_item (id, payment_attempt_id, item_type, contract_id, amount, status)
            VALUES ({Guid.NewGuid()}, {serviceCheckout}, 'CONTRACT', {h.ContractId}, 1000, 'SETTLED')
            """);
        Assert.Equal(("fk_payment_attempt_item_attempt_contract", PostgresErrorCodes.ForeignKeyViolation), (foreign.ConstraintName, foreign.SqlState));

        // The matching checkout accepts one CONTRACT line, never two.
        await using (var db = database.NewContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO payment_attempt_item (id, payment_attempt_id, item_type, contract_id, amount, status)
                VALUES ({Guid.NewGuid()}, {contractCheckout.Id}, 'CONTRACT', {h.ContractId}, {contractCheckout.Amount}, 'SETTLED')
                """);
        var twice = await SqlFailsAsync(database, $"""
            INSERT INTO payment_attempt_item (id, payment_attempt_id, item_type, contract_id, amount, status)
            VALUES ({Guid.NewGuid()}, {contractCheckout.Id}, 'CONTRACT', {h.ContractId}, {contractCheckout.Amount}, 'PENDING')
            """);
        Assert.Equal(("ux_payment_attempt_item_attempt_contract", PostgresErrorCodes.UniqueViolation), (twice.ConstraintName, twice.SqlState));
    }

    [PostgresFact]
    public async Task Stored_checkout_and_request_references_must_point_to_existing_rows()
    {
        // Arrange: an active maintenance term with planned visits.
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance));
        var visit = await ctx.Db.MaintenanceEntitlements.AsNoTracking().FirstAsync(e => e.SubscriptionId == term.Id);
        var missing = Guid.NewGuid();

        // Act / Assert
        var attempt = await SqlFailsAsync(database, $"UPDATE service_subscription SET current_payment_attempt_id = {missing} WHERE id = {term.Id}");
        Assert.Equal("fk_service_subscription_current_payment_attempt", attempt.ConstraintName);
        var request = await SqlFailsAsync(database, $"UPDATE maintenance_entitlement SET current_request_id = {missing} WHERE id = {visit.Id}");
        Assert.Equal("fk_maintenance_entitlement_current_request", request.ConstraintName);
        // The normal workflow writes both sides in one transaction, so the keys never get in its way.
        Assert.NotNull(term.CurrentPaymentAttemptId);
    }
}
