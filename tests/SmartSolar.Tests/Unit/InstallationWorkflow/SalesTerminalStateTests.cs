using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// M-1 / D-03 unsuccessful-sales outcomes: customer rejection, customer withdrawal, Sales closure (NOT_FEASIBLE and others). Terminal states
/// record actor, time and reason, keep history, leave the active queues and are never reopened; a request with a contract is never closed.
/// SQLite only: the PostgreSQL behaviour of the conditional updates under real concurrency is NOT EXECUTED here.
/// </summary>
public sealed class SalesTerminalStateTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, QuotationCatalog Catalog, Quotation Issued)> IssuedAsync(
        string feasibility = "FEASIBLE")
    {
        var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;
        var survey = await ctx.ConfirmedSurveyAsync();
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        await AutomaticQuotationFixture.CompleteAsync(ctx, survey, catalog.StandardBill(), feasibility: feasibility);
        var issued = (await AutomaticQuotationFixture.QuotationsAsync(ctx, survey.Request.Request.Id)).SingleOrDefault()!;
        return (ctx, survey, catalog, issued);
    }

    [Fact]
    public async Task A_customer_rejects_an_issued_quotation_which_then_can_never_be_accepted_or_reissued()
    {
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        var customer = s.Request.Customer.Id;
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(10);

        Assert.Equal(CommonValidation, (await q.RejectAsync(customer, issued.Id, new string('x', 2001), None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.RejectAsync(s.Sales.Id, issued.Id, "not mine", None)).ErrorCode);
        var rejected = (await q.RejectAsync(customer, issued.Id, "Too expensive", None)).Value!;
        Assert.Equal((QuotationStatuses.Rejected, false, "Too expensive"), (rejected.Status, rejected.CanAccept, rejected.CloseReason));
        var row = await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id);
        Assert.Equal((QuotationStatuses.Rejected, (Guid?)customer, (DateTimeOffset?)ctx.Clock.GetUtcNow()), (row.Status, row.ClosedByUserId, row.ClosedAt));
        Assert.Single(await ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == issued.Id && e.EventType == QuotationEventTypes.Rejected).ToListAsync());

        // Terminal: accepting, revising-by-request or issuing the rejected version is refused; repeating the rejection changes nothing.
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.AcceptAsync(customer, issued.Id, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.RequestRevisionAsync(customer, issued.Id, "please", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.IssueAsync(s.Sales.Id, issued.Id, row.Revision, None)).ErrorCode);
        Assert.True((await q.RejectAsync(customer, issued.Id, "again", None)).Succeeded);
        Assert.Equal("Too expensive", (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id)).CloseReason);

        // The request stays open: Sales may answer with a new version (once); the rejected one keeps its outcome.
        var v2 = (await q.CreateRevisionAsync(s.Sales.Id, issued.Id, None)).Value!;
        Assert.Equal(QuotationErrorCodes.CannotRevise, (await q.CreateRevisionAsync(s.Sales.Id, issued.Id, None)).ErrorCode);
        Assert.True((await q.IssueAsync(s.Sales.Id, v2.QuotationId, v2.Revision, None)).Succeeded);
        Assert.Equal(new[] { (1, QuotationStatuses.Rejected), (2, QuotationStatuses.Issued) },
            (await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id)).Select(x => (x.Version, x.Status)));
        // The automatic generation never answers a rejection by itself.
        Assert.Equal(AutomaticQuotationOutcomes.AlreadyGenerated, (await q.GenerateStandardQuotationAsync(s.Request.Request.Id, s.Sales.Id, None)).Outcome);
    }

    private const string CommonValidation = "VALIDATION_FAILED";

    [Fact]
    public async Task Reject_and_accept_of_the_same_issued_quotation_cannot_both_succeed()
    {
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        var customer = s.Request.Customer.Id;
        // Both requests start from the same ISSUED read; the conditional ISSUED → … updates let exactly one win (sequential here: SQLite has one
        // connection; the true parallel race needs PostgreSQL and is NOT EXECUTED).
        var accepted = await ctx.Quotations().AcceptAsync(customer, issued.Id, None);
        var rejected = await ctx.Quotations().RejectAsync(customer, issued.Id, "changed my mind", None);
        Assert.True(accepted.Succeeded);
        Assert.Equal(QuotationErrorCodes.NotIssued, rejected.ErrorCode);
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id)).Status);

        var (ctx2, s2, _, issued2) = await IssuedAsync();
        await using var __ = ctx2;
        Assert.True((await ctx2.Quotations().RejectAsync(s2.Request.Customer.Id, issued2.Id, null, None)).Succeeded);
        Assert.Equal(QuotationErrorCodes.NotIssued, (await ctx2.Quotations().AcceptAsync(s2.Request.Customer.Id, issued2.Id, None)).ErrorCode);
    }

    [Fact]
    public async Task A_customer_withdraws_an_open_request_which_cancels_its_open_quotations_keeps_history_and_is_never_reopened()
    {
        var (ctx, s, catalog, issued) = await IssuedAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        var customer = s.Request.Customer.Id;
        var stranger = await ctx.Scheduling.Inner.SeedUserAsync("Stranger");

        Assert.Equal(SalesOutcomeErrorCodes.RequestNotFound, (await q.WithdrawRequestAsync(stranger.Id, s.Request.Request.Id, null, None)).ErrorCode);
        Assert.Equal(404, OperationResultControllerBase.StatusFor(SalesOutcomeErrorCodes.RequestNotFound));
        var withdrawn = (await q.WithdrawRequestAsync(customer, s.Request.Request.Id, "We are moving house", None)).Value!;
        Assert.Equal((nameof(SurveyRequestStatus.Cancelled), SurveyRequestClosureReasons.CustomerWithdrew, "We are moving house", issued.Id),
            (withdrawn.Status, withdrawn.ReasonCode, withdrawn.Note, Assert.Single(withdrawn.CancelledQuotationIds)));
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == s.Request.Request.Id);
        Assert.Equal((SurveyRequestStatus.Cancelled, (Guid?)customer), (request.Status, request.ClosedByUserId));
        Assert.Single(await ctx.Db.SurveyRequestEvents.AsNoTracking().Where(e => e.SurveyRequestId == request.Id && e.EventType == SurveyRequestEventTypes.RequestWithdrawn).ToListAsync());
        var quotation = await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id);
        Assert.Equal((QuotationStatuses.Cancelled, "CUSTOMER_WITHDREW: We are moving house"), (quotation.Status, quotation.CloseReason));

        // Repeating the withdrawal returns the same outcome; any other closure or quotation work is refused.
        Assert.True((await q.WithdrawRequestAsync(customer, s.Request.Request.Id, null, None)).Succeeded);
        Assert.Equal(SalesOutcomeErrorCodes.RequestClosed, (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "NO_RESPONSE", null, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.RequestClosed, (await q.CreateRevisionAsync(s.Sales.Id, issued.Id, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.RequestClosed, (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id,
            new QuotationHeaderInput(1m, 0m, 0m, 0m, 0m, ctx.Clock.Now.AddDays(5), null, null), None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.AcceptAsync(customer, issued.Id, None)).ErrorCode);
        var onSite = ctx.OnSiteWithAutomaticQuotation();
        var report = (await onSite.GetForTechnicianAsync(s.Technician.Id, s.AppointmentId, None)).Value!;
        var amend = await onSite.AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "late change",
            AutomaticQuotationFixture.Data(catalog.StandardBill(panels: 3)), null, report.Revision), None);
        Assert.Equal(OnSiteSurveyErrorCodes.RequestClosed, amend.ErrorCode);

        // History stays readable: the customer still sees the cancelled quotation; Sales still lists it.
        Assert.Equal(QuotationStatuses.Cancelled, (await q.GetForCustomerAsync(customer, issued.Id, None)).Value!.Status);
        Assert.Single((await q.ListForSurveyRequestAsync(s.Sales.Id, false, s.Request.Request.Id, None)).Value!);
    }

    [Fact]
    public async Task After_a_withdrawal_the_customer_starts_a_new_request_and_the_old_one_stays_final_with_nothing_copied()
    {
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        var customer = s.Request.Customer.Id;
        Assert.True((await ctx.Quotations().WithdrawRequestAsync(customer, s.Request.Request.Id, "Not now", None)).Succeeded);

        // B-01: a new request for the same site is a new pre-survey and a new survey request; the closed one is never reopened.
        var created = await ctx.Scheduling.Inner.CreatePreSurveyHandler().HandleAsync(
            new SmartSolar.Modules.PreSurvey.CreatePreSurvey.CreatePreSurveyCommand(customer, s.Request.Site.Id, 120m, 90m, 15m, 180m, false), None);
        Assert.Equal(SmartSolar.Modules.PreSurvey.CreatePreSurvey.CreatePreSurveyOutcome.Created, created.Outcome);
        var submitted = await ctx.Scheduling.Inner.SubmitPreSurveyHandler().HandleAsync(
            new SmartSolar.Modules.PreSurvey.SubmitPreSurvey.SubmitPreSurveyCommand(customer, created.PreSurveyId!.Value), None);
        Assert.Equal("Submitted", submitted.Outcome.ToString());
        var requests = await ctx.Db.SurveyRequests.AsNoTracking().Where(r => r.PreSurvey.Property.Id == s.Request.Site.Id).ToListAsync();
        Assert.Equal(2, requests.Count);
        var old = requests.Single(r => r.Id == s.Request.Request.Id);
        var fresh = requests.Single(r => r.Id != s.Request.Request.Id);
        Assert.Equal((SurveyRequestStatus.Cancelled, SurveyRequestClosureReasons.CustomerWithdrew), (old.Status, old.ClosureReasonCode));
        Assert.Equal((SurveyRequestStatus.Pending, (Guid?)null, (string?)null), (fresh.Status, fresh.AssignedSaleId, fresh.ClosureReasonCode));
        // Nothing technical or commercial is carried over; the old history is intact.
        Assert.Empty(await ctx.Db.Quotations.AsNoTracking().Where(q => q.SurveyRequestId == fresh.Id).ToListAsync());
        Assert.Empty(await ctx.Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == fresh.Id).ToListAsync());
        Assert.Equal(QuotationStatuses.Cancelled, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == issued.Id)).Status);
    }

    [Fact]
    public async Task A_request_with_a_paid_contract_or_an_open_appointment_cannot_be_withdrawn_or_closed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == paid.ContractId);
        var sales = (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == contract.SurveyRequestId)).AssignedSaleId!.Value;
        var q = ctx.Quotations();

        Assert.Equal(SalesOutcomeErrorCodes.ContractExists, (await q.WithdrawRequestAsync(paid.CustomerId, contract.SurveyRequestId, null, None)).ErrorCode);
        Assert.Equal(SalesOutcomeErrorCodes.ContractExists, (await q.CloseRequestAsync(sales, contract.SurveyRequestId, "CUSTOMER_DECLINED", null, None)).ErrorCode);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == paid.ContractId)).Status);
        Assert.Empty(await ctx.Db.RefundRecords.AsNoTracking().ToListAsync());
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == contract.QuotationId)).Status);

        // A scheduled request (accepted appointment) must have its appointment cancelled first.
        var ctx2 = InstallationWorkflowTestContext.Create();
        await using var __ = ctx2;
        var scheduled = await ctx2.ConfirmedSurveyAsync("Second customer");
        Assert.Equal(SalesOutcomeErrorCodes.AppointmentOpen,
            (await ctx2.Quotations().WithdrawRequestAsync(scheduled.Request.Customer.Id, scheduled.Request.Request.Id, null, None)).ErrorCode);
        Assert.Equal(SalesOutcomeErrorCodes.AppointmentOpen,
            (await ctx2.Quotations().CloseRequestAsync(scheduled.Sales.Id, scheduled.Request.Request.Id, "NO_RESPONSE", null, None)).ErrorCode);
        Assert.Equal(SurveyRequestStatus.Scheduled, (await ctx2.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == scheduled.Request.Request.Id)).Status);
    }

    [Fact]
    public async Task Sales_closes_a_not_feasible_request_with_a_reason_and_it_leaves_every_active_queue()
    {
        var (ctx, s, _, _) = await IssuedAsync(feasibility: "NOT_FEASIBLE");
        await using var _ = ctx;
        var q = ctx.Quotations();
        var otherSales = await ctx.Scheduling.SeedSalesAsync("Other sales");

        Assert.Equal(CommonValidation, (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "BORED", null, None)).ErrorCode);
        Assert.Equal(CommonValidation, (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "OTHER", " ", None)).ErrorCode);
        Assert.Equal(SalesOutcomeErrorCodes.RequestNotFound, (await q.CloseRequestAsync(otherSales.Id, s.Request.Request.Id, "NOT_FEASIBLE", null, None)).ErrorCode);
        var closed = (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "not_feasible", "Roof cannot carry the load", None)).Value!;
        Assert.Equal((nameof(SurveyRequestStatus.Closed), SurveyRequestClosureReasons.NotFeasible), (closed.Status, closed.ReasonCode));
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == s.Request.Request.Id);
        Assert.Equal((SurveyRequestStatus.Closed, (Guid?)s.Sales.Id, "Roof cannot carry the load"), (request.Status, request.ClosedByUserId, request.ClosureNote));
        var closeEvent = await ctx.Db.SurveyRequestEvents.AsNoTracking().SingleAsync(e => e.SurveyRequestId == request.Id && e.EventType == SurveyRequestEventTypes.RequestClosed);
        Assert.Equal("NOT_FEASIBLE: Roof cannot carry the load", closeEvent.Reason);

        // Not in the unclaimed queue, not in the quotation review queue, nothing can be generated for it.
        Assert.DoesNotContain(await ctx.Scheduling.Inner.UnitOfWork.GetPendingSurveyRequestsAsync(None), r => r.SurveyRequestId == request.Id);
        Assert.Empty(await q.ListPendingReviewAsync(s.Sales.Id, false, None));
        Assert.Equal(AutomaticQuotationOutcomes.RequestClosed, (await q.GenerateStandardQuotationAsync(request.Id, s.Sales.Id, None)).Outcome);
    }

    [Fact]
    public async Task Not_feasible_is_only_a_closure_reason_for_a_not_feasible_survey_and_other_reasons_cancel_open_quotations()
    {
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        Assert.Equal(SalesOutcomeErrorCodes.NotFeasibleUnconfirmed, (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "NOT_FEASIBLE", null, None)).ErrorCode);
        var closed = (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "NO_RESPONSE", null, None)).Value!;
        Assert.Equal(issued.Id, Assert.Single(closed.CancelledQuotationIds));
        Assert.Equal(QuotationStatuses.Cancelled, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id)).Status);
        Assert.True((await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "NO_RESPONSE", null, None)).Succeeded);   // same outcome: idempotent
        Assert.Equal(SalesOutcomeErrorCodes.RequestClosed, (await q.WithdrawRequestAsync(s.Request.Customer.Id, s.Request.Request.Id, null, None)).ErrorCode);
    }

    [Fact]
    public async Task Closing_a_request_cancels_an_open_draft_that_was_never_issued()
    {
        // Found by the PostgreSQL race test: a never-issued draft has no issue fields, and CANCELLED must still be a valid state for it.
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        var q = ctx.Quotations();
        Assert.True((await q.RejectAsync(s.Request.Customer.Id, issued.Id, "Too expensive", None)).Succeeded);
        var draft = (await q.CreateRevisionAsync(s.Sales.Id, issued.Id, None)).Value!;
        var closed = (await q.CloseRequestAsync(s.Sales.Id, s.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).Value!;
        Assert.Equal(draft.QuotationId, Assert.Single(closed.CancelledQuotationIds));
        var row = await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == draft.QuotationId);
        Assert.Equal((QuotationStatuses.Cancelled, (DateTimeOffset?)null), (row.Status, row.IssuedAt));
        Assert.Equal(QuotationStatuses.Rejected, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id)).Status);
    }

    [Fact]
    public async Task The_database_refuses_terminal_states_without_their_closure_metadata()
    {
        var (ctx, s, _, issued) = await IssuedAsync();
        await using var _ = ctx;
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quotation SET status = 'REJECTED' WHERE id = {issued.Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE survey_request SET status = 'Closed' WHERE id = {s.Request.Request.Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE survey_request SET status = 'Closed', closed_at = '2026-10-31T00:00:00+00:00', closed_by_user_id = {s.Sales.Id}, closure_reason_code = 'BORED' WHERE id = {s.Request.Request.Id}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE survey_request SET closed_at = '2026-10-31T00:00:00+00:00', closure_reason_code = 'NO_RESPONSE' WHERE id = {s.Request.Request.Id}"));
        // A legacy Cancelled row without metadata stays valid.
        Assert.Equal(1, await ctx.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE survey_request SET status = 'Cancelled' WHERE id = {s.Request.Request.Id}"));
    }
}
