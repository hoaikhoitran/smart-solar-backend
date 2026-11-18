using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// NFR (local PostgreSQL 17): with 2,000 additional 30/70 contracts (cloned from one real journey, each with its two installments, half of them with an
/// overdue balance) the staff directory answers one page with filters / search in SQL, and the finance overview aggregates everything, within the
/// read budgets below. Figures are recorded in guides/payment-30-70/BACKEND_CHANGE_REPORT.md; budgets are generous on purpose (shared laptop).
/// </summary>
public sealed class PostgresPaymentPlanVolumeTests
{
    private const int Clones = 2_000;
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task NFR_directory_page_and_finance_overview_stay_within_budget_with_2000_contracts()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var flow = await AgreedAsync(ctx);
        await PayCashAsync(ctx, flow, 1);

        // Clone the real quotation / contract / installments 2,000 times (unique numbers and versions; triggers stay enabled).
        await using (var db = database.NewContext())
        {
            db.Database.SetCommandTimeout(300);
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TEMP TABLE clone_ids AS
                SELECT g, gen_random_uuid() AS quotation_id, gen_random_uuid() AS contract_id FROM generate_series(1, {Clones}) g;
                INSERT INTO quotation
                SELECT (jsonb_populate_record(NULL::quotation, to_jsonb(q) || jsonb_build_object('id', c.quotation_id, 'quote_number', 'BG-NFR-' || c.g,
                    'version', 100 + c.g, 'status', 'SUPERSEDED'))).*
                FROM quotation q CROSS JOIN clone_ids c WHERE q.id = '{flow.Cart.Quotation.QuotationId}';
                ALTER TABLE customer_contract DISABLE TRIGGER USER;
                INSERT INTO customer_contract
                SELECT (jsonb_populate_record(NULL::customer_contract, to_jsonb(k) || jsonb_build_object('id', c.contract_id, 'quotation_id', c.quotation_id,
                    'contract_number', 'HD-NFR-' || c.g, 'status', CASE WHEN c.g % 2 = 0 THEN 'SIGNED' ELSE 'DRAFT' END,
                    'signed_at', CASE WHEN c.g % 2 = 0 THEN now() ELSE NULL END, 'issued_at', CASE WHEN c.g % 2 = 0 THEN now() ELSE NULL END))).*
                FROM customer_contract k CROSS JOIN clone_ids c WHERE k.id = '{flow.ContractId}';
                ALTER TABLE customer_contract ENABLE TRIGGER USER;
                INSERT INTO contract_installment (id, contract_id, sequence, kind, percent, amount, status, opened_at, due_at, created_at, updated_at, revision, paid_late)
                SELECT gen_random_uuid(), c.contract_id, i.sequence, i.kind, i.percent, i.amount,
                       CASE WHEN i.sequence = 2 AND c.g % 2 = 0 THEN 'DUE' WHEN i.sequence = 2 THEN 'PENDING' ELSE 'DUE' END,
                       CASE WHEN i.sequence = 1 OR c.g % 2 = 0 THEN now() - interval '5 days' ELSE NULL END,
                       CASE WHEN i.sequence = 2 AND c.g % 2 = 0 THEN now() - interval '2 days' ELSE NULL END, now(), now(), 0, false
                FROM contract_installment i CROSS JOIN clone_ids c WHERE i.contract_id = '{flow.ContractId}';
                ANALYZE customer_contract; ANALYZE contract_installment;
                """);
        }
        ctx.Clock.Now = DateTimeOffset.UtcNow;

        var directory = ctx.Directory();
        await directory.PageAsync(new ContractListFilter(), new PageRequest(1, 20), None);   // warm-up (connection, plan cache)
        var watch = Stopwatch.StartNew();
        var page = (await directory.PageAsync(new ContractListFilter(Overdue: true), new PageRequest(3, 50), None)).Value!;
        var search = (await directory.PageAsync(new ContractListFilter(Search: "HD-NFR-1999"), new PageRequest(1, 20), None)).Value!;
        var directoryMs = watch.ElapsedMilliseconds;
        Assert.Equal(Clones / 2, page.TotalItems);
        Assert.Equal(50, page.Items.Count);
        Assert.All(page.Items, i => Assert.True(i.Overdue));
        Assert.Single(search.Items);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        watch.Restart();
        var overview = (await ctx.Finance().GetAsync(today.AddDays(-30), today, null, None)).Value!;
        var financeMs = watch.ElapsedMilliseconds;
        Assert.Equal(Clones + 1, overview.PaymentPlanContracts.Count);
        Assert.Equal(Clones / 2, overview.BalancesOverdue.Count);

        Assert.True(directoryMs < 3_000, $"directory: two pages took {directoryMs} ms");
        Assert.True(financeMs < 5_000, $"finance overview took {financeMs} ms");
        Console.WriteLine($"NFR 30/70 volume ({Clones} contracts): directory 2 pages {directoryMs} ms; finance overview {financeMs} ms");
    }
}
