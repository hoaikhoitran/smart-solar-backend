using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// M-7: the Sales owner explicitly discards an initial DRAFT contract (reason, actor, time; row kept as DISCARDED), after which the
/// survey request can be closed. A discarded contract is never issued, signed, paid, amended or replaced; binding contracts keep blocking the
/// closure. SQLite (handler level); the PostgreSQL races are in PostgresSalesClosureConcurrencyTests.
/// </summary>
public sealed class ContractDraftDiscardTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(InstallationWorkflowTestContext Ctx, ContractTests.Ready Ready, ContractView Draft)> DraftAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        var r = await ContractTests.AcceptedQuotationAsync(ctx);
        var draft = (await ctx.Contracts().CreateAsync(r.Survey.Sales.Id, r.QuotationId, "Standard terms", None)).Value!;
        return (ctx, r, draft);
    }

    [Fact]
    public async Task Accepted_quotation_to_draft_contract_to_discard_to_closed_request()
    {
        var (ctx, r, draft) = await DraftAsync();
        await using var _ = ctx;
        var contracts = ctx.Contracts();
        var quotations = ctx.Quotations();
        var requestId = r.Survey.Request.Request.Id;
        var otherSales = await ctx.Scheduling.SeedSalesAsync("Other sales");

        // Closing does not silently discard: with a DRAFT it is refused and says so.
        var refused = await quotations.CloseRequestAsync(r.Survey.Sales.Id, requestId, "CUSTOMER_DECLINED", "Customer withdrew", None);
        Assert.Equal(SalesOutcomeErrorCodes.ContractExists, refused.ErrorCode);
        Assert.Contains("DRAFT", System.Text.Json.JsonSerializer.Serialize(refused.Details));

        // Validation, ownership, staleness.
        Assert.Equal(ContractErrorCodes.DiscardReasonRequired, (await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, " ", None)).ErrorCode);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(ContractErrorCodes.DiscardReasonRequired));
        Assert.Equal(ContractErrorCodes.NotFound, (await contracts.DiscardAsync(otherSales.Id, draft.ContractId, draft.Revision, "x", None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.StaleRevision, (await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision + 1, "x", None)).ErrorCode);

        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(3);
        var discarded = (await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, "Customer withdrew before the contract was issued.", None)).Value!;
        Assert.Equal(ContractStatuses.Discarded, discarded.Status);
        Assert.Equal((ctx.Clock.GetUtcNow(), r.Survey.Sales.Id, "Customer withdrew before the contract was issued."),
            (discarded.Discard!.DiscardedAt, discarded.Discard.DiscardedByUserId, discarded.Discard.Reason));
        var row = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == draft.ContractId);
        Assert.Equal((ContractStatuses.Discarded, (DateTimeOffset?)null, "Standard terms"), (row.Status, row.IssuedAt, row.Terms));   // kept, never issued

        // A repeat (even with the old revision) changes nothing and writes no second event.
        Assert.True((await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, "again", None)).Succeeded);
        Assert.Single(await ctx.Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == draft.ContractId && e.ToStatus == ContractStatuses.Discarded).ToListAsync());
        Assert.Equal("Customer withdrew before the contract was issued.", (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == draft.ContractId)).DiscardReason);

        // Now the request can be closed, explicitly; it leaves the queues and the history is intact.
        var closed = (await quotations.CloseRequestAsync(r.Survey.Sales.Id, requestId, "CUSTOMER_DECLINED", "Customer withdrew", None)).Value!;
        Assert.Equal(nameof(SurveyRequestStatus.Closed), closed.Status);
        Assert.Equal(r.QuotationId, Assert.Single(closed.CancelledQuotationIds));
        Assert.Equal(QuotationStatuses.Cancelled, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == r.QuotationId)).Status);
        Assert.Equal(ContractStatuses.Discarded, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == draft.ContractId)).Status);
        Assert.Equal(r.QuotationId, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == draft.ContractId)).QuotationId);
        Assert.DoesNotContain(await ctx.Scheduling.Inner.UnitOfWork.GetPendingSurveyRequestsAsync(None), x => x.SurveyRequestId == requestId);
        Assert.Empty(await quotations.ListPendingReviewAsync(r.Survey.Sales.Id, false, None));
    }

    [Fact]
    public async Task A_discarded_contract_is_never_issued_signed_paid_amended_replaced_or_shown_to_the_customer()
    {
        var (ctx, r, draft) = await DraftAsync();
        await using var _ = ctx;
        var contracts = ctx.Contracts();
        var discarded = (await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, "Customer withdrew", None)).Value!;
        var customer = r.Survey.Request.Customer.Id;

        Assert.Equal(ContractErrorCodes.InvalidState, (await contracts.IssueAsync(r.Survey.Sales.Id, draft.ContractId, discarded.Revision, None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.InvalidState, (await contracts.UpdateTermsAsync(r.Survey.Sales.Id, draft.ContractId, discarded.Revision, "new", None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.InvalidState, (await contracts.CreateAmendmentAsync(r.Survey.Sales.Id, draft.ContractId, "new", None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.DraftDiscarded, (await contracts.CreateAsync(r.Survey.Sales.Id, r.QuotationId, "again", None)).ErrorCode);
        var scan = await ctx.VerifiedFileAsync(customer, SmartSolar.Modules.Files.FilePurposes.SignedContract, "pdf");
        Assert.False((await contracts.SubmitSignedDocumentAsync(customer, draft.ContractId, scan, new string('a', 64), None)).Succeeded);
        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await ctx.Payments().CreateOrGetAttemptAsync(customer, draft.ContractId, None)).ErrorCode);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await ctx.Installations().CreateProjectAsync(manager.Id, draft.ContractId, None)).ErrorCode);
        Assert.Empty(await ctx.Installations().ListReadyContractsAsync(None));
        // Customers never saw the draft and do not see the discarded one.
        Assert.Equal(ContractErrorCodes.NotFound, (await contracts.GetForCustomerAsync(customer, draft.ContractId, None)).ErrorCode);
        Assert.Empty(await contracts.ListForCustomerAsync(customer, None));
        Assert.Equal(ContractStatuses.Discarded, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == draft.ContractId)).Status);
        Assert.Empty(await ctx.Db.PaymentAttempts.AsNoTracking().Where(a => a.ContractId == draft.ContractId).ToListAsync());
    }

    [Fact]
    public async Task Issued_signed_paid_and_amendment_contracts_cannot_be_discarded_and_keep_blocking_the_closure()
    {
        // Issued.
        var (ctx, r, draft) = await DraftAsync();
        await using (ctx)
        {
            var contracts = ctx.Contracts();
            var issued = (await contracts.IssueAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, None)).Value!;
            var refused = await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, issued.Revision, "too late", None);
            Assert.Equal(ContractErrorCodes.NotDiscardable, refused.ErrorCode);
            Assert.Equal(409, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
            Assert.Equal(SalesOutcomeErrorCodes.ContractExists,
                (await ctx.Quotations().CloseRequestAsync(r.Survey.Sales.Id, r.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).ErrorCode);

            // An amendment draft next to the issued contract: not discardable here.
            var amendment = (await contracts.CreateAmendmentAsync(r.Survey.Sales.Id, issued.ContractId, "Amended terms", None)).Value!;
            Assert.Equal(ContractErrorCodes.NotDiscardable, (await contracts.DiscardAsync(r.Survey.Sales.Id, amendment.ContractId, amendment.Revision, "x", None)).ErrorCode);
            Assert.Equal(ContractStatuses.Draft, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == amendment.ContractId)).Status);
        }

        // Paid.
        var ctx2 = InstallationWorkflowTestContext.Create();
        await using var __ = ctx2;
        var paid = await InstallationTests.PaidContractAsync(ctx2);
        var contract = await ctx2.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == paid.ContractId);
        var sales = (await ctx2.Db.SurveyRequests.AsNoTracking().SingleAsync(s => s.Id == contract.SurveyRequestId)).AssignedSaleId!.Value;
        Assert.Equal(ContractErrorCodes.NotDiscardable, (await ctx2.Contracts().DiscardAsync(sales, paid.ContractId, contract.Revision, "x", None)).ErrorCode);
        Assert.Equal(ContractStatuses.Paid, (await ctx2.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == paid.ContractId)).Status);
        Assert.Single(await ctx2.Db.PaymentAttempts.AsNoTracking().Where(a => a.ContractId == paid.ContractId && a.Status == PaymentStatuses.Paid).ToListAsync());
    }

    [Fact]
    public async Task A_failed_discard_can_be_retried_and_the_database_rejects_inconsistent_discard_data()
    {
        var (ctx, r, draft) = await DraftAsync();
        await using var _ = ctx;
        var contracts = ctx.Contracts();
        var edited = (await contracts.UpdateTermsAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, "Edited terms", None)).Value!;
        Assert.Equal(ContractErrorCodes.StaleRevision, (await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, "x", None)).ErrorCode);
        Assert.True((await contracts.DiscardAsync(r.Survey.Sales.Id, draft.ContractId, edited.Revision, "Customer withdrew", None)).Succeeded);
        Assert.Single(await ctx.Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == draft.ContractId && e.ToStatus == ContractStatuses.Discarded).ToListAsync());

        var (ctx2, r2, draft2) = await DraftAsync();
        await using var __ = ctx2;
        var id = draft2.ContractId;
        await Assert.ThrowsAnyAsync<Exception>(() => ctx2.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customer_contract SET status = 'DISCARDED' WHERE id = {id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx2.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer_contract SET discarded_at = '2026-10-31T00:00:00+00:00', discarded_by_user_id = {r2.Survey.Sales.Id}, discard_reason = 'x' WHERE id = {id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx2.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer_contract SET status = 'DISCARDED', discarded_at = '2026-10-31T00:00:00+00:00', discarded_by_user_id = {r2.Survey.Sales.Id}, discard_reason = 'x', issued_at = '2026-10-31T00:00:00+00:00' WHERE id = {id}"));
        Assert.Equal(1, await ctx2.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer_contract SET status = 'DISCARDED', discarded_at = '2026-10-31T00:00:00+00:00', discarded_by_user_id = {r2.Survey.Sales.Id}, discard_reason = 'x' WHERE id = {id}"));
    }
}
