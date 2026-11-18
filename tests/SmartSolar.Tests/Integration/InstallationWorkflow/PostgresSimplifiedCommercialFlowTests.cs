using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SimplifiedFlowFixture;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// The 2026-11-17 simplified flow on a disposable local PostgreSQL database (never a shared one): concurrent callers each on their own
/// connection, and the SimplifiedCommercialFlow migration Up / Down. PayOS and storage are synthetic.
/// </summary>
public sealed class PostgresSimplifiedCommercialFlowTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261115153617_PasswordResetDispatchOutbox";
    private const string Migration = "20261117074228_SimplifiedCommercialFlow";

    // ------------------------------------------------------------------ races

    [PostgresFact]
    public async Task Concurrent_payment_starts_leave_one_live_payment_and_concurrent_cash_confirmations_create_exactly_one_contract()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var cart = await IssuedAsync(ctx);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        await AgreedAsync(ctx, cart);

        // Six taps on "take cash": the quotation row lock leaves exactly one live payment (repeats return it or are refused).
        var starts = await RaceAsync(database, 6, (db, _) => ctx.OnSitePayments(db).StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None));
        Assert.Contains(starts, r => r.Succeeded);
        Assert.All(starts.Where(r => !r.Succeeded), r => Assert.Equal(PaymentErrorCodes.CheckoutInProgress, r.ErrorCode));
        await using (var check = database.NewContext())
            Assert.Equal(1, await check.PaymentAttempts.CountAsync());

        // Six concurrent cash confirmations: one PAID attempt, one settled line, exactly one contract.
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var evidence = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence);
        var attemptId = starts.First(r => r.Succeeded).Value!.Current!.PaymentAttemptId;
        var confirmations = await RaceAsync(database, 6, (db, i) => ctx.PaymentsWithCash(db).ConfirmCashAttemptAsync(manager.Id, attemptId,
            new CashConfirmation(cart.Quotation.TotalAmount, "PT-RACE-1", evidence, null), None));
        Assert.Single(confirmations, r => r.Succeeded);
        await using var after = database.NewContext();
        Assert.Equal(1, await after.CustomerContracts.CountAsync(c => c.QuotationId == cart.Quotation.QuotationId));
        Assert.Equal(1, await after.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await after.PaymentAttemptItems.CountAsync(i => i.Status == PaymentItemStatuses.Settled && i.InstallationQuotationId == cart.Quotation.QuotationId));
        Assert.Equal(1, await after.ContractEvents.CountAsync(e => e.EventType == "PAID_BEFORE_CONTRACT"));
    }

    [PostgresFact]
    public async Task Concurrent_cart_edits_cannot_both_win()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var cart = await DraftAsync(ctx);

        var results = await RaceAsync(database, 4, (db, i) => ctx.Quotations(db).AddCartLineAsync(cart.TechnicianId, cart.Quotation.QuotationId, cart.Quotation.Revision,
            i % 2 == 0 ? cart.Catalog.Panel.Id : cart.Catalog.Inverter.Id, i + 1, None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(QuotationErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.QuotationLines.CountAsync());
    }

    [PostgresFact]
    public async Task Choosing_cash_and_opening_an_online_link_at_once_leaves_exactly_one_payment_method()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SignedAsync(ctx);
        var customer = signed.Cart.CustomerId;
        var contractId = signed.Contract.ContractId;

        var results = await RaceAsync(database, 6, (db, i) => i % 2 == 0
            ? ctx.PaymentsWithCash(db).SelectCashAsync(customer, contractId, None)
            : ctx.PaymentsWithCash(db).CreateOrGetAttemptAsync(customer, contractId, None));

        await using var check = database.NewContext();
        var live = await check.PaymentAttempts.Where(a => a.ContractId == contractId && (a.Status == PaymentStatuses.Pending || a.Status == PaymentStatuses.Processing)).ToListAsync();
        var winner = Assert.Single(live);
        // Every caller either got that very attempt or a "the other method is in progress" refusal.
        Assert.All(results, r => Assert.True(r.Succeeded ? r.Value!.PaymentAttemptId == winner.Id
            : r.ErrorCode is PaymentErrorCodes.CashSelected or PaymentErrorCodes.CheckoutInProgress, r.ErrorCode));
        Assert.Equal(ContractStatuses.Signed, (await check.CustomerContracts.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task Concurrent_cash_confirmations_pay_the_contract_exactly_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        Assert.True((await ctx.PaymentsWithCash().SelectCashAsync(signed.Cart.CustomerId, contractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);

        var results = await RaceAsync(database, 5, (db, i) => ctx.PaymentsWithCash(db).ConfirmCashAsync(signed.Manager.Id, contractId,
            new CashConfirmation(signed.Contract.TotalAmount, $"PT-RACE-{i}", evidence, null), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.True(r.ErrorCode is PaymentErrorCodes.AlreadyPaid or PaymentErrorCodes.CashNotSelected, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.ContractId == contractId && a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == contractId && e.ToStatus == ContractStatuses.Paid));
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task A_cash_confirmation_racing_the_customer_cancelling_the_cash_choice_has_one_consistent_outcome()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        Assert.True((await ctx.PaymentsWithCash().SelectCashAsync(signed.Cart.CustomerId, contractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);

        await RaceAsync(database, 2, async (db, i) => i == 0
            ? (object)await ctx.PaymentsWithCash(db).ConfirmCashAsync(signed.Manager.Id, contractId, new CashConfirmation(signed.Contract.TotalAmount, "PT-X", evidence, null), None)
            : await ctx.PaymentsWithCash(db).CancelCurrentAsync(signed.Cart.CustomerId, contractId, None));

        await using var check = database.NewContext();
        var attempt = await check.PaymentAttempts.SingleAsync(a => a.ContractId == contractId);
        var contract = await check.CustomerContracts.SingleAsync();
        Assert.True((attempt.Status, contract.Status) is (PaymentStatuses.Paid, ContractStatuses.Paid) or (PaymentStatuses.Cancelled, ContractStatuses.Signed),
            $"{attempt.Status} / {contract.Status}");
    }

    [PostgresFact]
    public async Task A_late_online_payment_delivered_concurrently_after_cash_opens_one_refund_and_settles_nothing()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        await using var _ = database;
        await using var after = AfterSalesTestContext.CreateOn(database.NewContext());
        var ctx = after.Installation;
        var signed = await SignedAsync(ctx);
        var customer = signed.Cart.CustomerId;
        var contractId = signed.Contract.ContractId;
        var online = (await after.Payments().CreateOrGetAttemptAsync(customer, contractId, None)).Value!;
        Assert.True((await after.Payments().CancelCurrentAsync(customer, contractId, None)).Succeeded);
        Assert.True((await after.Payments().SelectCashAsync(customer, contractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);
        Assert.True((await after.Payments().ConfirmCashAsync(signed.Manager.Id, contractId, new(signed.Contract.TotalAmount, "PT-L", evidence, null), None)).Succeeded);
        var body = ctx.PayOs.Webhook(online.OrderCode, (long)online.Amount, "FT-LATE");

        await RaceAsync(database, 4, (db, _) => after.Payments(db).HandleWebhookAsync(body, None));

        await using var check = database.NewContext();
        Assert.Equal(PaymentStatuses.RefundRequired, (await check.PaymentAttempts.SingleAsync(a => a.Id == online.PaymentAttemptId)).Status);
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.ContractId == contractId && a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await check.RefundRecords.CountAsync(r => r.PaymentAttemptId == online.PaymentAttemptId));
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync()).Status);
    }

    // ------------------------------------------------------------------ migration

    private static Task MigrateToAsync(AppDbContext db, string migration) => db.GetService<IMigrator>().MigrateAsync(migration);

    private static async Task<List<string>> SchemaAsync(AppDbContext db) => await db.Database.SqlQueryRaw<string>("""
        SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable || ' default=' || coalesce(column_default, '') AS "Value"
        FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
        UNION ALL
        SELECT 'constraint ' || conrelid::regclass::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace = 'public'::regnamespace
        UNION ALL
        SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'public'
        """).OrderBy(x => x).ToListAsync();

    [PostgresFact]
    public async Task The_migration_keeps_existing_rows_Down_restores_the_previous_schema_and_Up_runs_again()
    {
        await using var before = await PostgresTestDatabase.CreateAsync(upToMigration: Previous);
        await using var full = await PostgresTestDatabase.CreateAsync();
        await using var expectedDb = before.NewContext();
        var expected = await SchemaAsync(expectedDb);
        await using var db = full.NewContext();
        var upgraded = await SchemaAsync(db);
        Assert.Contains("column quotation.sales_user_id uuid null=YES default=", upgraded);
        Assert.Contains("column customer_contract.sales_user_id uuid null=YES default=", upgraded);
        Assert.Contains(upgraded, l => l.StartsWith("constraint payment_attempt ck_payment_attempt_cash_confirmation", StringComparison.Ordinal));

        await MigrateToAsync(db, Previous);
        Assert.Equal(expected, await SchemaAsync(db));
        await db.Database.MigrateAsync();
        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync());
    }

    private static Task<List<string>> RowsAsync(AppDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT 'quotation ' || row_to_json(q)::text AS "Value" FROM quotation q
        UNION ALL SELECT 'line ' || row_to_json(l)::text FROM quotation_line l
        -- 30/70 columns (2026-11-19) are new and NULL on every upgraded row (asserted separately); the comparison covers every older column.
        UNION ALL SELECT 'contract ' || (to_jsonb(c) - 'payment_plan' - 'terms_version')::text FROM customer_contract c
        UNION ALL SELECT 'contract_event ' || row_to_json(e)::text FROM contract_event e
        """).OrderBy(x => x).ToListAsync();

    [PostgresFact]
    public async Task Legacy_sales_quotations_and_contracts_survive_the_upgrade_unchanged()
    {
        // Legacy data: a Sales quotation, accepted, with its Sales contract. It is written with the current model, then the database is
        // migrated DOWN to the previous schema (the newer migrations accept such legacy-only data), so the rows are exactly as the previous
        // version stored them before the upgrade under test.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (survey, issued) = await Unit.InstallationWorkflow.QuotationTests.IssuedAsync(ctx);
        await LegacyFlowData.AcceptedWithoutContractAsync(ctx.Db, issued.QuotationId, ctx.Clock.GetUtcNow());
        var ready = new Unit.InstallationWorkflow.ContractTests.Ready(survey, issued.QuotationId, issued.TotalAmount);
        await using var db = database.NewContext();
        // The contract row as the retired Sales flow stored it (with its Sales owner).
        db.CustomerContracts.Add(new CustomerContract
        {
            Id = Guid.NewGuid(), ContractNumber = "HD-LEGACY-0001", Version = 1, QuotationId = ready.QuotationId, QuotationVersion = 1,
            SurveyRequestId = ready.Survey.Request.Request.Id, CustomerUserId = ready.Survey.Request.Customer.Id, SalesUserId = ready.Survey.Sales.Id,
            Status = ContractStatuses.Draft, Currency = "VND", TotalAmount = ready.Total, PricingSnapshot = "{}", CustomerSnapshot = "{}", Terms = "Legacy terms",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await MigrateToAsync(db, Previous);
        var rowsBefore = await RowsAsync(db);
        Assert.Contains(rowsBefore, r => r.Contains(ready.Survey.Sales.Id.ToString()));

        await db.Database.MigrateAsync();

        await using var check = database.NewContext();
        Assert.Equal(rowsBefore, await RowsAsync(check));
        Assert.All(await check.CustomerContracts.AsNoTracking().ToListAsync(), c => Assert.Equal((null, null), (c.PaymentPlan, c.TermsVersion)));
        Assert.Equal(ready.Survey.Sales.Id, (await check.Quotations.AsNoTracking().SingleAsync(q => q.Id == ready.QuotationId)).SalesUserId);
        Assert.Equal(ready.Survey.Sales.Id, (await check.CustomerContracts.AsNoTracking().SingleAsync()).SalesUserId);
    }

    [PostgresFact]
    public async Task Down_is_refused_while_data_of_the_new_flow_exists_and_changes_nothing()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var cart = await DraftAsync(ctx);
        await using var db = database.NewContext();
        // The later migration (direct booking / installation schedule) has no data of its own here and goes down first.
        await MigrateToAsync(db, Migration);
        var schemaBefore = await SchemaAsync(db);

        var refused = await Record.ExceptionAsync(() => MigrateToAsync(db, Previous));

        Assert.NotNull(refused);
        Assert.Contains("SimplifiedCommercialFlow rollback refused", refused.ToString());
        await using var check = database.NewContext();
        Assert.Contains(Migration, await check.Database.GetAppliedMigrationsAsync());
        Assert.Equal(schemaBefore, await SchemaAsync(check));
        Assert.Equal(QuotationOrigins.Technician, (await check.Quotations.SingleAsync(q => q.Id == cart.Quotation.QuotationId)).Origin);
    }
}
