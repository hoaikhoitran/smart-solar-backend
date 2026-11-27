using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>Technician cash receipts on a disposable PostgreSQL database: constraints, partial unique indexes and concurrent review (2026-11-27).</summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresTechnicianCashReceiptTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Concurrent_submissions_leave_exactly_one_receipt_under_review()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await TechnicianCashReceiptTests.CashChosenAsync(ctx, flow, 1);
        var photos = new List<Guid>();
        for (var i = 0; i < 4; i++) photos.Add(await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence));

        var results = await Task.WhenAll(photos.Select((photo, i) => Task.Run(async () =>
        {
            await using var db = database.NewContext();
            return await ctx.InstallmentPayments(db).SubmitCashReceiptAsync(flow.TechnicianId, flow.ContractId, 1,
                new CashReceiptInput(attemptId, $"PT-RACE-{i}", photo, null, null), None);
        })));
        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[] { CashReceiptErrorCodes.AlreadySubmitted, PaymentErrorCodes.CashNotSelected }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.CashReceiptSubmissions.CountAsync(r => r.Status == CashReceiptStatuses.Submitted));
        Assert.Equal(PaymentStatuses.Pending, (await check.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
    }

    [PostgresFact]
    public async Task Concurrent_accepts_and_a_reject_apply_the_money_exactly_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await TechnicianCashReceiptTests.CashChosenAsync(ctx, flow, 1);
        var receipt = await TechnicianCashReceiptTests.SentAsync(ctx, flow, 1, attemptId, "PT-ONCE");

        var accepts = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var db = database.NewContext();
            var r = await ctx.PaymentsWithCash(db).AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, null, None);
            return (r.Succeeded, r.ErrorCode);
        }));
        var reject = Task.Run(async () =>
        {
            await using var db = database.NewContext();
            var r = await ctx.PaymentsWithCash(db).RejectCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, "race", None);
            return (r.Succeeded, r.ErrorCode);
        });
        var outcomes = await Task.WhenAll(accepts.Append(reject));
        Assert.Single(outcomes, o => o.Succeeded);

        await using var check = database.NewContext();
        var stored = await check.CashReceiptSubmissions.AsNoTracking().SingleAsync();
        var attempt = await check.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        var installment = await check.ContractInstallments.AsNoTracking().SingleAsync(i => i.ContractId == flow.ContractId && i.Sequence == 1);
        if (stored.Status == CashReceiptStatuses.Accepted)
        {
            Assert.Equal(PaymentStatuses.Paid, attempt.Status);
            Assert.Equal(InstallmentStatuses.Paid, installment.Status);
            Assert.Equal(1, await check.PaymentAttemptItems.CountAsync(i => i.PaymentAttemptId == attemptId && i.Status == PaymentItemStatuses.Settled));
        }
        else
        {
            Assert.Equal(CashReceiptStatuses.Rejected, stored.Status);
            Assert.Equal(PaymentStatuses.Pending, attempt.Status);
            Assert.Equal(InstallmentStatuses.Due, installment.Status);
        }
    }

    [PostgresFact]
    public async Task The_database_refuses_inconsistent_receipt_rows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await TechnicianCashReceiptTests.CashChosenAsync(ctx, flow, 1);
        var receipt = await TechnicianCashReceiptTests.SentAsync(ctx, flow, 1, attemptId, "PT-DB-1");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        async Task<string> FailsAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState;
        }
        string Copy(string status, string number, string extra = "NULL, NULL, NULL")
            => $"INSERT INTO cash_receipt_submission (id, payment_attempt_id, contract_id, contract_installment_id, amount, receipt_number, collected_by_user_id, collected_at, "
               + $"evidence_file_id, note, status, submitted_at, reviewed_by_user_id, reviewed_at, review_reason, revision) "
               + $"SELECT gen_random_uuid(), payment_attempt_id, contract_id, contract_installment_id, amount, '{number}', collected_by_user_id, collected_at, evidence_file_id, NULL, "
               + $"'{status}', submitted_at, {extra}, 1 FROM cash_receipt_submission WHERE id = '{receipt.CashReceiptId}'";
        // A second open receipt for the same payment, a live duplicate number, a rejection without reason or reviewer, an unknown status.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await FailsAsync(Copy(CashReceiptStatuses.Submitted, "PT-DB-2")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await FailsAsync(Copy(CashReceiptStatuses.Rejected, "PT-DB-3", $"'{flow.Manager.Id}', now(), '  '")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await FailsAsync(Copy(CashReceiptStatuses.Rejected, "PT-DB-4", "NULL, now(), 'blurred'")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await FailsAsync(Copy("PAID", "PT-DB-5", $"'{flow.Manager.Id}', now(), NULL")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await FailsAsync($"UPDATE cash_receipt_submission SET receipt_number = '   ' WHERE id = '{receipt.CashReceiptId}'"));
        // A reviewed copy with the same live number (ACCEPTED) is refused by the live-number index.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await FailsAsync(Copy(CashReceiptStatuses.Accepted, "PT-DB-1", $"'{flow.Manager.Id}', now(), NULL")));
        // RLS is enabled like every application table.
        await using var rls = new NpgsqlCommand("SELECT relrowsecurity FROM pg_class WHERE relname = 'cash_receipt_submission'", connection);
        Assert.True((bool)(await rls.ExecuteScalarAsync())!);
    }
}
