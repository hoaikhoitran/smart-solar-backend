using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.AfterSales;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>Unified cash receipts on a disposable PostgreSQL database: concurrency on an after-sales quotation and migration compatibility (2026-11-27).</summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresUnifiedCashReceiptTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261127021802_TechnicianCashReceipts";

    [PostgresFact]
    public async Task UC17_concurrent_submissions_for_one_quotation_leave_exactly_one_receipt_under_review()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var photos = new List<Guid>();
        for (var i = 0; i < 4; i++) photos.Add(await ctx.Installation.VerifiedFileAsync(t.H.TechnicianId, FilePurposes.PaymentEvidence));
        var results = await Task.WhenAll(photos.Select((photo, i) => Task.Run(async () =>
        {
            await using var db = database.NewContext();
            return await ctx.Quotations(db).SubmitCashReceiptOnSiteAsync(t.H.TechnicianId, t.Quotation.QuotationId,
                new CashReceiptInput(attemptId, $"PT-ASR-{i}", photo, null, null), None);
        })));
        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[] { CashReceiptErrorCodes.AlreadySubmitted, PaymentErrorCodes.CashNotSelected }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.CashReceiptSubmissions.CountAsync(r => r.Status == CashReceiptStatuses.Submitted && r.ServiceQuotationId == t.Quotation.QuotationId));
    }

    [PostgresFact]
    public async Task UC18_concurrent_approvals_and_a_rejection_settle_the_quotation_exactly_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var receipt = await UnifiedCashReceiptTests.SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId);
        var calls = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var db = database.NewContext();
            return (await ctx.Payments(db).AcceptCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None)).Succeeded;
        })).Append(Task.Run(async () =>
        {
            await using var db = database.NewContext();
            return (await ctx.Payments(db).RejectCashReceiptAsync(t.H.ManagerId, receipt.CashReceiptId, receipt.Revision, "race", None)).Succeeded;
        }));
        Assert.Single(await Task.WhenAll(calls), ok => ok);
        await using var check = database.NewContext();
        var stored = await check.CashReceiptSubmissions.AsNoTracking().SingleAsync();
        var quotation = await check.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == t.Quotation.QuotationId);
        var attempt = await check.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        if (stored.Status == CashReceiptStatuses.Accepted)
        {
            Assert.Equal((QuotationStatuses.Paid, PaymentStatuses.Paid), (quotation.Status, attempt.Status));
            Assert.Equal(1, await check.PaymentAttemptItems.CountAsync(i => i.PaymentAttemptId == attemptId && i.Status == PaymentItemStatuses.Settled));
            Assert.Equal(1, await check.AfterSalesEvents.CountAsync(e => e.SubjectId == t.RequestId && e.EventType == "QUOTATION_PAID"));
        }
        else
        {
            Assert.Equal((CashReceiptStatuses.Rejected, QuotationStatuses.Accepted, PaymentStatuses.Pending), (stored.Status, quotation.Status, attempt.Status));
        }
    }

    [PostgresFact]
    public async Task UC28_existing_installment_receipts_survive_up_down_up_and_down_is_refused_once_after_sales_receipts_exist()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        // An installment receipt (the only kind before this migration), then back to the previous schema with the row in place.
        await using (var installation = InstallationWorkflowTestContext.CreateOn(database.NewContext()))
        {
            var flow = await PaymentPlanFlow.AgreedAsync(installation);
            var attemptId = await TechnicianCashReceiptTests.CashChosenAsync(installation, flow, 1);
            await TechnicianCashReceiptTests.SentAsync(installation, flow, 1, attemptId, "PT-LEGACY-1");
        }
        await using (var down = database.NewContext()) await down.GetService<IMigrator>().MigrateAsync(Previous);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        async Task<object?> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'cash_receipt_submission' AND column_name = 'kind'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM cash_receipt_submission WHERE receipt_number = 'PT-LEGACY-1' AND contract_id IS NOT NULL AND contract_installment_id IS NOT NULL"));

        // Up again: the stored row becomes an INSTALLMENT receipt, unchanged otherwise, and satisfies the new context constraint.
        await using (var up = database.NewContext()) await up.Database.MigrateAsync();
        Assert.Equal("INSTALLMENT", await ScalarAsync("SELECT kind FROM cash_receipt_submission WHERE receipt_number = 'PT-LEGACY-1'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM pg_constraint WHERE conname = 'ck_cash_receipt_submission_context'"));
        await using (var read = database.NewContext())
        {
            var row = await read.CashReceiptSubmissions.AsNoTracking().SingleAsync(r => r.ReceiptNumber == "PT-LEGACY-1");
            Assert.Equal((CashReceiptKinds.Installment, (Guid?)null, (Guid?)null), (row.Kind, row.ServiceQuotationId, row.ServiceRequestId));
            Assert.NotNull(row.ContractInstallmentId);
        }
        // A row with a mixed context is refused by the database.
        await using (var mixed = new NpgsqlCommand("UPDATE cash_receipt_submission SET kind = 'SERVICE_QUOTATION' WHERE receipt_number = 'PT-LEGACY-1'", connection))
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => mixed.ExecuteNonQueryAsync())).SqlState);

        // Once an after-sales receipt exists, Down is refused and nothing changes.
        await using (var afterSales = AfterSalesTestContext.CreateOn(database.NewContext()))
        {
            var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(afterSales);
            var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(afterSales, t.H.TechnicianId, t.Quotation.QuotationId);
            await UnifiedCashReceiptTests.SentAsync(afterSales, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-AS-NEW");
        }
        await using (var refused = database.NewContext())
            Assert.Contains("Down refused", (await Assert.ThrowsAnyAsync<Exception>(() => refused.GetService<IMigrator>().MigrateAsync(Previous))).Message);
        await using (var after = database.NewContext())
        {
            Assert.Empty(await after.Database.GetPendingMigrationsAsync());
            Assert.Equal(2, await after.CashReceiptSubmissions.CountAsync());
        }
    }
}
