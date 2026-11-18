using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// AFTER-QUOTE / AFTER-PAYMENT on a disposable PostgreSQL database (2026-11-18): persistence of technician quotations, concurrent issues leave one open
/// version, concurrent cash confirmations collect once, cash and online never both start, and the migration's check and rollback guard hold.
/// </summary>
public sealed class PostgresAfterSalesRemediationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261117171006_InstallationSlotOffers";
    private const string Migration = "20261117185657_AfterSalesTechnicianQuotation";

    private static async Task<(PostgresTestDatabase Database, AfterSalesTestContext Ctx)> WorldAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        return (database, AfterSalesTestContext.CreateOn(database.NewContext()));
    }

    private static async Task<(HandedOver H, Guid RequestId, ServiceQuotationView Quotation)> AcceptedAsync(AfterSalesTestContext ctx)
    {
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 2))], null, null), None)).Value!.Quotations);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        return (h, requestId, q);
    }

    [PostgresFact]
    public async Task Concurrent_technician_issues_leave_one_open_version_and_the_quotation_is_persisted_with_its_catalog_snapshot()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var input = new TechnicianQuotationInput([Line(items[0], (breaker.Id, 2))], null, null);

        var results = await RaceAsync(database, 4, (db, _) => AfterSalesTestContext.CreateOn(db).Quotations(db).IssueByTechnicianAsync(h.TechnicianId, requestId, input, None));

        Assert.Contains(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(AfterSalesErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        var open = await check.ServiceQuotations.SingleAsync(q => q.Status == QuotationStatuses.Issued);
        Assert.Equal((QuotationIssuers.Technician, 300_000m, h.TechnicianId), (open.IssuedByRole, open.TotalAmount, open.SalesUserId));
        Assert.Contains("BRK-C16", open.LinesJson);   // jsonb round trip keeps the snapshot
        Assert.Equal(await check.ServiceQuotations.CountAsync(), (await check.ServiceQuotations.Select(q => q.Version).ToListAsync()).Distinct().Count());
        // The unclaimed Sales task was cancelled (no owner), not completed.
        Assert.Equal(SalesTaskStatuses.Cancelled, (await check.SalesTasks.SingleAsync(t => t.RequestId == requestId)).Status);
    }

    [PostgresFact]
    public async Task Concurrent_cash_confirmations_collect_once_and_settle_the_quotation_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (h, requestId, q) = await AcceptedAsync(ctx);
        var attempt = (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!.PaymentAttemptId;
        var receipts = new List<CashConfirmation>();
        for (var i = 0; i < 6; i++)
            receipts.Add(new(300_000m, $"PT-RACE-{i}", await ctx.Installation.VerifiedFileAsync(h.ManagerId, FilePurposes.PaymentEvidence), null));

        var results = await RaceAsync(database, receipts.Count, (db, i) => AfterSalesTestContext.CreateOn(db).Payments(db).ConfirmCashAttemptAsync(h.ManagerId, attempt, receipts[i], None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[] { PaymentErrorCodes.AlreadyPaid, PaymentErrorCodes.CashNotSelected }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await check.PaymentAttemptItems.CountAsync(i => i.QuotationId == q.QuotationId && i.Status == PaymentItemStatuses.Settled));
        Assert.Equal(QuotationStatuses.Paid, (await check.ServiceQuotations.SingleAsync(x => x.Id == q.QuotationId)).Status);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await check.ServiceRequests.SingleAsync(r => r.Id == requestId)).Status);
        Assert.Equal(0, await check.RefundRecords.CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_cash_choices_and_online_checkouts_leave_one_live_payment_for_the_quotation()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (h, _, q) = await AcceptedAsync(ctx);

        var results = await RaceAsync(database, 6, (db, i) =>
        {
            var quotations = AfterSalesTestContext.CreateOn(db).Quotations(db);
            return i % 2 == 0 ? quotations.SelectCashAsync(h.CustomerId, q.QuotationId, None) : quotations.CheckoutAsync(h.CustomerId, q.QuotationId, None);
        });

        Assert.Contains(results, r => r.Succeeded);
        await using var check = database.NewContext();
        var live = await check.PaymentAttempts.Where(a => a.ContractId == null && (a.Status == PaymentStatuses.Pending || a.Status == PaymentStatuses.Processing)).ToListAsync();
        var current = (await check.ServiceQuotations.SingleAsync(x => x.Id == q.QuotationId)).CurrentPaymentAttemptId;
        // Losing attempts may exist only as non-payable rows; the quotation points to the single live one.
        Assert.Single(live);
        Assert.Equal(live[0].Id, current);
    }

    [PostgresFact]
    public async Task MIGRATION_The_issuer_check_holds_existing_rows_default_to_SALES_and_Down_refuses_while_technician_quotations_exist()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        await AcceptedAsync(ctx);

        await using var db = database.NewContext();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var failure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("UPDATE service_quotation SET issued_by_role = 'CUSTOMER'"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(failure).SqlState);
            await tx.RollbackAsync();
        }
        Assert.Equal("SALES", await db.Database.SqlQueryRaw<string>(
            "SELECT column_default::text AS \"Value\" FROM information_schema.columns WHERE table_name = 'service_quotation' AND column_name = 'issued_by_role'")
            .Select(v => v.Contains("SALES") ? "SALES" : v).SingleAsync());

        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.NotNull(refused);
        await using var after = database.NewContext();
        Assert.Contains(Migration, await after.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await after.ServiceQuotations.CountAsync(x => x.IssuedByRole == QuotationIssuers.Technician));
    }

    [PostgresFact]
    public async Task MIGRATION_Down_and_Up_round_trip_without_technician_quotations()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'service_quotation' AND column_name = 'issued_by_role') AS \"Value\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task DASHBOARD_The_overview_translates_on_PostgreSQL_and_counts_a_confirmed_after_sales_cash_payment()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (h, _, q) = await AcceptedAsync(ctx);
        var attempt = (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!.PaymentAttemptId;
        var evidence = await ctx.Installation.VerifiedFileAsync(h.ManagerId, FilePurposes.PaymentEvidence);
        Assert.True((await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, attempt, new(300_000m, "PT-PG", evidence, null), None)).Succeeded);

        var overview = new AdminOverview(new SmartSolar.Infrastructure.Persistence.AfterSales.AdminOverviewQueries(database.NewContext()),
            ctx.Installation.Scheduling.Calendar, ctx.Clock);
        var today = ctx.Installation.Scheduling.Calendar.LocalDateOf(ctx.Clock.GetUtcNow());
        var view = (await overview.GetAsync(today.AddDays(-60), today.AddDays(1), None)).Value!;

        var cash = view.CollectedByMethod.Single(b => b.Key == PaymentMethods.Cash);
        Assert.Equal((1, 300_000m), (cash.Count, cash.Amount));
        Assert.Equal(1, view.ServiceQuotationsByIssuer.Single(b => b.Key == QuotationIssuers.Technician).Count);
        Assert.Equal(1, view.ServiceQuotationsByStatus.Single(b => b.Key == QuotationStatuses.Paid).Count);
        Assert.NotEmpty(view.Accounts);
        Assert.NotEmpty(view.SurveyRequestsByStatus);
    }
}
