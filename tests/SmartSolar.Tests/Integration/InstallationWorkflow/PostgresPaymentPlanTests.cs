using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// MIGRATION 20261118202100_PaymentPlan3070PaperSignatureCollection on a disposable PostgreSQL database: the full 30/70 flow passes the database
/// guards (deferred triggers), the guards refuse what the older API or manual SQL could do (100% payment of a 30/70 contract, skipping states, PAID
/// without a settled line), the constraints and indexes hold, races settle exactly once, Down refuses while 30/70 data exists and Down / Up
/// round-trips on a database without it.
/// </summary>
public sealed class PostgresPaymentPlanTests
{
    private const string Previous = "20261118102011_TechnicianLedOnSiteSales";
    private const string Migration = "20261118202100_PaymentPlan3070PaperSignatureCollection";
    /// <summary>The latest migration (a clean database is fully migrated).</summary>
    private const string Latest = "20261123140208_SharedKnowledgePublicationScope";
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var db = database.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            // Deferred constraint triggers fire at commit: force them now (the transaction is rolled back anyway).
            await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL IMMEDIATE");
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [PostgresFact]
    public async Task PG_full_30_70_flow_passes_the_database_guards_and_ends_fully_paid()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow, payOnline: true);
        await AcceptAsync(ctx, flow, projectId);
        ctx.Clock.Now = (await InstallmentAsync(ctx, flow.ContractId, 2)).DueAt!.Value.AddHours(1);
        Assert.Equal(1, await ctx.Ledger().SweepOverdueAsync(10, None));
        await PayCashAsync(ctx, flow, 2, InstallmentPayer.Customer);

        await using var read = database.NewContext();
        var contract = await read.CustomerContracts.SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, PaymentPlans.Deposit30Balance70), (contract.Status, contract.PaymentPlan));
        Assert.All(await read.ContractInstallments.Where(i => i.ContractId == flow.ContractId).ToListAsync(), i => Assert.Equal(InstallmentStatuses.Paid, i.Status));
        Assert.Equal(CollectionCaseStatuses.Resolved, (await read.ContractCollectionCases.SingleAsync()).Status);
        Assert.Equal(contract.TotalAmount, await read.PaymentAttemptItems.Where(i => i.Status == PaymentItemStatuses.Settled).SumAsync(i => i.Amount));
    }

    [PostgresFact]
    public async Task PG_guards_refuse_skipped_states_manual_paid_and_a_100_percent_payment_of_a_30_70_contract()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var flow = await AgreedAsync(ctx);
        var id = flow.ContractId;
        const string check = "23514", unique = "23505";

        // The older API (or manual SQL) can never: pay the contract 100%, issue / publish it, mark it PAID or SIGNED, or mark an installment PAID.
        Assert.Equal(check, await SqlStateAsync(database, $"""
            INSERT INTO payment_attempt (id, contract_id, customer_user_id, provider, order_code, amount, currency, description, status, expires_at, created_at, updated_at, revision)
            SELECT gen_random_uuid(), c.id, c.customer_user_id, 'PAYOS', 99887766, c.total_amount, 'VND', 'SS0000001', 'PENDING', now() + interval '15 minutes', now(), now(), 0
            FROM customer_contract c WHERE c.id = '{id}'
            """));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE customer_contract SET status = 'ISSUED', issued_at = now() WHERE id = '{id}'"));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE customer_contract SET status = 'AWAITING_SIGNATURE', issued_at = now() WHERE id = '{id}'"));   // deposit not paid
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE customer_contract SET status = 'PAID', paid_at = now(), signed_at = now(), issued_at = now() WHERE id = '{id}'"));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE customer_contract SET payment_plan = NULL WHERE id = '{id}'"));
        Assert.Equal(check, await SqlStateAsync(database, $"""
            UPDATE contract_installment SET status = 'PAID', paid_at = now(), paid_amount = amount, settled_payment_attempt_id = gen_random_uuid()
            WHERE contract_id = '{id}' AND sequence = 1
            """));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE contract_installment SET amount = amount + 1 WHERE contract_id = '{id}' AND sequence = 1"));
        Assert.Equal(unique, await SqlStateAsync(database, $"""
            INSERT INTO contract_installment (id, contract_id, sequence, kind, percent, amount, status, opened_at, created_at, updated_at, revision, paid_late)
            VALUES (gen_random_uuid(), '{id}', 1, 'DEPOSIT', 30, 1, 'DUE', now(), now(), now(), 0, false)
            """));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE contract_installment SET kind = 'BALANCE' WHERE contract_id = '{id}' AND sequence = 1"));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE contract_installment SET status = 'DUE' WHERE contract_id = '{id}' AND sequence = 2"));   // balance due needs due_at
        Assert.Equal(check, await SqlStateAsync(database, "UPDATE customer_contract SET payment_plan = 'DEPOSIT_50'"));

        // After the real deposit + paper signature, PAID still needs the balance.
        await PayCashAsync(ctx, flow, 1);
        await SignOnPaperAsync(ctx, flow, 1);
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE customer_contract SET status = 'PAID', paid_at = now() WHERE id = '{id}'"));
        Assert.Equal(check, await SqlStateAsync(database, $"UPDATE contract_installment SET status = 'DUE' WHERE contract_id = '{id}' AND sequence = 1"));   // paid stays paid
        // Settled line uniqueness per installment and one current signature set / page.
        Assert.Equal(unique, await SqlStateAsync(database, """
            INSERT INTO payment_attempt_item (id, payment_attempt_id, item_type, contract_installment_id, amount, status)
            SELECT gen_random_uuid(), payment_attempt_id, item_type, contract_installment_id, amount, 'SETTLED' FROM payment_attempt_item WHERE contract_installment_id IS NOT NULL
            """));
        Assert.Equal(check, await SqlStateAsync(database, "UPDATE payment_attempt_item SET contract_installment_id = NULL WHERE contract_installment_id IS NOT NULL"));
        Assert.Equal(unique, await SqlStateAsync(database, $"""
            INSERT INTO contract_signature_set (id, contract_id, version, expected_page_count, status, created_by_user_id, created_at, updated_at, revision)
            SELECT gen_random_uuid(), contract_id, 9, 1, 'UPLOADING', created_by_user_id, now(), now(), 0 FROM contract_signature_set WHERE contract_id = '{id}'
            """));
        // A legacy contract (payment_plan NULL) is untouched by the guards.
        Assert.Null(await SqlStateAsync(database, $"UPDATE customer_contract SET updated_at = now() WHERE id = '{id}'"));
    }

    [PostgresFact]
    public async Task PG_concurrent_agreements_create_one_contract_and_concurrent_deposit_starts_open_one_payment()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        await ctx.EnsureInstallationWarrantyOptionAsync();

        var agreed = await RaceAsync(database, 6, (db, _) => ctx.QuotationsWithPaymentPlan(db).RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, null), None));
        Assert.Contains(agreed, r => r.Succeeded);
        await using (var read = database.NewContext())
        {
            Assert.Equal(1, await read.CustomerContracts.CountAsync());
            Assert.Equal(2, await read.ContractInstallments.CountAsync());
        }
        var contractId = agreed.First(r => r.Succeeded).Value!.ContractId!.Value;
        Assert.All(agreed.Where(r => r.Succeeded), r => Assert.Equal(contractId, r.Value!.ContractId));

        var started = await RaceAsync(database, 6, (db, i) => ctx.InstallmentPayments(db).StartAsync(InstallmentPayer.Technician, cart.TechnicianId, contractId, 1,
            i % 2 == 0 ? "CASH" : "ONLINE", None));
        await using var check = database.NewContext();
        var payable = await check.PaymentAttempts.Where(a => a.Status == PaymentStatuses.Pending || a.Status == PaymentStatuses.Processing).ToListAsync();
        Assert.Single(payable);
        Assert.Contains(started, r => r.Succeeded);
    }

    [PostgresFact]
    public async Task PG_balance_webhook_racing_a_cash_confirmation_settles_the_installment_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);
        // A QR is shown, then cancelled locally (customer switches to cash); the bank app had paid it anyway.
        var qr = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "ONLINE", None)).Value!.Current!;
        Assert.True((await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, None)).Succeeded);
        var cash = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "CASH", None)).Value!.Current!;
        var evidence = await ctx.VerifiedFileAsync(flow.Manager.Id, SmartSolar.Modules.Files.FilePurposes.PaymentEvidence);

        await RaceAsync(database, 2, async (db, i) =>
        {
            if (i == 0) await ctx.PaymentsWithSchedule(db).ConfirmAsync(qr.PaymentAttemptId, (long)qr.Amount, "FT-RACE", DateTimeOffset.UtcNow, PaymentSources.Webhook, None);
            else await ctx.PaymentsWithCash(db).ConfirmCashAttemptAsync(flow.Manager.Id, cash.PaymentAttemptId, new CashConfirmation(cash.Amount, "PT-RACE", evidence, null), None);
            return true;
        });

        await using var read = database.NewContext();
        var balance = await read.ContractInstallments.SingleAsync(i => i.ContractId == flow.ContractId && i.Sequence == 2);
        Assert.Equal(InstallmentStatuses.Paid, balance.Status);
        Assert.Equal(1, await read.PaymentAttemptItems.CountAsync(i => i.ContractInstallmentId == balance.Id && i.Status == PaymentItemStatuses.Settled));
        Assert.Equal(ContractStatuses.Paid, (await read.CustomerContracts.SingleAsync(c => c.Id == flow.ContractId)).Status);
        // Whatever won, the other money is never applied: either the webhook money is REVIEW_REQUIRED, or the cash confirmation was refused.
        var webhookLine = await read.PaymentAttemptItems.SingleAsync(i => i.PaymentAttemptId == qr.PaymentAttemptId);
        var cashAttempt = await read.PaymentAttempts.SingleAsync(a => a.Id == cash.PaymentAttemptId);
        Assert.True((webhookLine.Status == PaymentItemStatuses.Settled) != (cashAttempt.Status == PaymentStatuses.Paid),
            $"webhook line {webhookLine.Status}, cash attempt {cashAttempt.Status}");
    }

    [PostgresFact]
    public async Task PG_concurrent_overdue_sweeps_open_one_case_and_concurrent_finalizations_sign_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var flow = await AgreedAsync(ctx);
        await PayCashAsync(ctx, flow, 1);
        var signatures = ctx.PaperSignatures();
        await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(1, null, null), None);
        var page = await ctx.VerifiedFileAsync(flow.TechnicianId, SmartSolar.Modules.Files.FilePurposes.SignedContractPage);
        Assert.True((await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, page, None)).Succeeded);
        var finalized = await RaceAsync(database, 4, (db, _) => ctx.PaperSignatures(db).FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None));
        Assert.Single(finalized, r => r.Succeeded);
        await using (var read = database.NewContext())
            Assert.Equal(1, await read.ContractEvents.CountAsync(e => e.ContractId == flow.ContractId && e.EventType == "SIGNED_PAGES_FINALIZED"));

        // Balance opened by hand-made acceptance state is not needed: open it through the ledger as the acceptance would, then race sweeps.
        await using (var db = database.NewContext())
        {
            var balance = await db.ContractInstallments.SingleAsync(i => i.ContractId == flow.ContractId && i.Sequence == 2);
            balance.Status = InstallmentStatuses.Due;
            balance.OpenedAt = DateTimeOffset.UtcNow.AddDays(-5);
            balance.DueAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }
        ctx.Clock.Now = DateTimeOffset.UtcNow;
        var opened = await RaceAsync(database, 5, (db, _) => ctx.Ledger(db).SweepOverdueAsync(10, None));
        Assert.Equal(1, opened.Sum());
        await using var check = database.NewContext();
        Assert.Equal(1, await check.ContractCollectionCases.CountAsync());
    }

    [PostgresFact]
    public async Task MIGRATION_Down_refuses_while_30_70_data_exists_and_round_trips_without_it()
    {
        var (database, ctx) = await WorldAsync();
        await using (database)
        await using (ctx)
        {
            await AgreedAsync(ctx);
            await using var db = database.NewContext();
            var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
            Assert.Contains("30/70 payment-plan data exists", refused?.ToString());
            await using var after = database.NewContext();
            Assert.Equal(Migration, (await after.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(1, await after.CustomerContracts.CountAsync(c => c.PaymentPlan != null));
        }

        await using var empty = await PostgresTestDatabase.CreateAsync();
        await using var clean = empty.NewContext();
        Assert.Equal(Latest, (await clean.Database.GetAppliedMigrationsAsync()).Last());
        await clean.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await clean.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'contract_installment') AS \"Value\"").SingleAsync());
        Assert.False(await clean.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_payment_attempt_payment_plan_guard') AS \"Value\"").SingleAsync());
        await clean.Database.MigrateAsync();
        Assert.True(await clean.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_customer_contract_payment_plan_transition') AS \"Value\"").SingleAsync());
        Assert.Empty(await clean.Database.GetPendingMigrationsAsync());
    }
}
