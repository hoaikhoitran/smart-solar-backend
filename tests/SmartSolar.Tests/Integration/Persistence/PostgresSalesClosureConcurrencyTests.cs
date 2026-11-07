using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PostgresRace;

namespace SmartSolar.Tests.Integration.Persistence;

/// <summary>
/// Pauses a unit of work right after it took the survey-request lock (or the lock method named in <see cref="Wrap"/>), so a second
/// transaction can be started against the held lock. Every other member is forwarded unchanged.
/// </summary>
public class LockHoldProxy<T> : DispatchProxy where T : class
{
    public T Inner { get; set; } = null!;
    public string HoldOn { get; set; } = "LockSurveyRequestAsync";
    public TaskCompletionSource Locked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static (T Proxy, LockHoldProxy<T> Control) Wrap(T inner, string holdOn = "LockSurveyRequestAsync")
    {
        var proxy = Create<T, LockHoldProxy<T>>();
        var control = (LockHoldProxy<T>)(object)proxy;
        control.Inner = inner;
        control.HoldOn = holdOn;
        return (proxy, control);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        try
        {
            var result = method!.Invoke(Inner, args);
            return method.Name == HoldOn ? HoldAsync((Task)result!) : result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task HoldAsync(Task locking)
    {
        await locking;
        Locked.TrySetResult();
        await Release.Task;
    }
}

/// <summary>
/// Closure / withdrawal versus contract creation (P0): both take the survey-request row lock first and re-check under it, so in either order
/// exactly one wins and the stored state is consistent. Two real PostgreSQL connections; the second transaction is started while the first
/// holds the lock and is released only once PostgreSQL reports it waiting on that lock (no sleeps). Also reject vs accept, duplicate
/// closure and closure vs revision.
/// </summary>
public sealed class PostgresSalesClosureConcurrencyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record World(PostgresTestDatabase Database, InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, Guid QuotationId);

    /// <summary>Survey completed, quotation issued and accepted, installation warranty configured: ready for a contract, no contract yet.</summary>
    private static async Task<World> AcceptedAsync(bool accept = true)
    {
        var (database, ctx) = await WorldAsync();
        var (survey, issued) = await QuotationTests.IssuedAsync(ctx);
        if (accept) Assert.True((await ctx.Quotations().AcceptAsync(survey.Request.Customer.Id, issued.QuotationId, None)).Succeeded);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        return new World(database, ctx, survey, issued.QuotationId);
    }

    private static QuotationHandlers Quotations(World w, AppDbContext db, IQuotationUnitOfWork? unitOfWork = null)
        => new(unitOfWork ?? new QuotationUnitOfWork(db), w.Ctx.Files(db), w.Ctx.QuotationOptions, w.Ctx.Clock);

    private static ContractHandlers Contracts(World w, AppDbContext db, IContractUnitOfWork? unitOfWork = null)
        => new(unitOfWork ?? new ContractUnitOfWork(db), w.Ctx.Files(db), w.Ctx.Clock);

    /// <summary>Waits (bounded) until another session of this database is blocked on a lock.</summary>
    private static async Task WaitUntilBlockedOnLockAsync(PostgresTestDatabase database)
    {
        await using var probe = database.NewContext();
        for (var i = 0; i < 500; i++)
        {
            var waiting = await probe.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync();
            if (waiting > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("The second transaction never waited on the survey-request lock.");
    }

    private static async Task<(SurveyRequestStatus Request, string Quotation, List<string> Contracts)> StateAsync(World w)
    {
        await using var db = w.Database.NewContext();
        var request = (await db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == w.Survey.Request.Request.Id)).Status;
        var quotation = (await db.Quotations.AsNoTracking().SingleAsync(q => q.Id == w.QuotationId)).Status;
        var contracts = await db.CustomerContracts.AsNoTracking().Where(c => c.SurveyRequestId == w.Survey.Request.Request.Id).Select(c => c.Status).ToListAsync();
        return (request, quotation, contracts);
    }

    [PostgresFact]
    public async Task Closure_first_then_contract_creation_is_refused_and_nothing_is_left_half_done()
    {
        var w = await AcceptedAsync();
        await using var _ = w.Database;
        await using var dbA = w.Database.NewContext();
        await using var dbB = w.Database.NewContext();
        var (held, control) = LockHoldProxy<IQuotationUnitOfWork>.Wrap(new QuotationUnitOfWork(dbA));

        var closing = Quotations(w, dbA, held).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None);
        await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // fail, never hang, if the lock is not reached   // the closure holds the request lock
        var creating = Contracts(w, dbB).CreateAsync(w.Survey.Sales.Id, w.QuotationId, "Terms", None);
        await WaitUntilBlockedOnLockAsync(w.Database);
        control.Release.SetResult();
        var (closed, contract) = (await closing, await creating);

        Assert.True(closed.Succeeded, closed.Message);
        Assert.Equal(ContractErrorCodes.RequestClosed, contract.ErrorCode);
        Assert.Equal((SurveyRequestStatus.Closed, QuotationStatuses.Cancelled, 0), (await StateAsync(w)) is var s ? (s.Request, s.Quotation, s.Contracts.Count) : default);
    }

    [PostgresFact]
    public async Task Contract_creation_first_then_closure_is_refused_and_the_request_stays_open()
    {
        var w = await AcceptedAsync();
        await using var _ = w.Database;
        await using var dbA = w.Database.NewContext();
        await using var dbB = w.Database.NewContext();
        var (held, control) = LockHoldProxy<IContractUnitOfWork>.Wrap(new ContractUnitOfWork(dbA));

        var creating = Contracts(w, dbA, held).CreateAsync(w.Survey.Sales.Id, w.QuotationId, "Terms", None);
        await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // fail, never hang, if the lock is not reached   // the contract creation holds the request lock
        var closing = Quotations(w, dbB).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None);
        await WaitUntilBlockedOnLockAsync(w.Database);
        control.Release.SetResult();
        var (contract, closed) = (await creating, await closing);

        Assert.True(contract.Succeeded, contract.Message);
        Assert.Equal(SalesOutcomeErrorCodes.ContractExists, closed.ErrorCode);
        var s = await StateAsync(w);
        Assert.Equal((SurveyRequestStatus.Completed, QuotationStatuses.Accepted, ContractStatuses.Draft), (s.Request, s.Quotation, Assert.Single(s.Contracts)));
    }

    [PostgresFact]
    public async Task Withdrawal_and_contract_creation_exclude_each_other_in_both_orders()
    {
        // Withdrawal first.
        var w = await AcceptedAsync();
        await using (w.Database)
        {
            await using var dbA = w.Database.NewContext();
            await using var dbB = w.Database.NewContext();
            var (held, control) = LockHoldProxy<IQuotationUnitOfWork>.Wrap(new QuotationUnitOfWork(dbA));
            var withdrawing = Quotations(w, dbA, held).WithdrawRequestAsync(w.Survey.Request.Customer.Id, w.Survey.Request.Request.Id, "Changed plans", None);
            await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // fail, never hang, if the lock is not reached
            var creating = Contracts(w, dbB).CreateAsync(w.Survey.Sales.Id, w.QuotationId, "Terms", None);
            await WaitUntilBlockedOnLockAsync(w.Database);
            control.Release.SetResult();
            Assert.True((await withdrawing).Succeeded);
            Assert.Equal(ContractErrorCodes.RequestClosed, (await creating).ErrorCode);
            var s = await StateAsync(w);
            Assert.Equal((SurveyRequestStatus.Cancelled, QuotationStatuses.Cancelled, 0), (s.Request, s.Quotation, s.Contracts.Count));
        }

        // Contract first.
        var w2 = await AcceptedAsync();
        await using (w2.Database)
        {
            await using var dbA = w2.Database.NewContext();
            await using var dbB = w2.Database.NewContext();
            var (held, control) = LockHoldProxy<IContractUnitOfWork>.Wrap(new ContractUnitOfWork(dbA));
            var creating = Contracts(w2, dbA, held).CreateAsync(w2.Survey.Sales.Id, w2.QuotationId, "Terms", None);
            await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // fail, never hang, if the lock is not reached
            var withdrawing = Quotations(w2, dbB).WithdrawRequestAsync(w2.Survey.Request.Customer.Id, w2.Survey.Request.Request.Id, "Changed plans", None);
            await WaitUntilBlockedOnLockAsync(w2.Database);
            control.Release.SetResult();
            Assert.True((await creating).Succeeded);
            Assert.Equal(SalesOutcomeErrorCodes.ContractExists, (await withdrawing).ErrorCode);
            var s = await StateAsync(w2);
            Assert.Equal((SurveyRequestStatus.Completed, QuotationStatuses.Accepted, 1), (s.Request, s.Quotation, s.Contracts.Count));
        }
    }

    [PostgresFact]
    public async Task Reject_and_accept_racing_on_the_same_issued_quotation_have_exactly_one_winner()
    {
        var w = await AcceptedAsync(accept: false);
        await using var _ = w.Database;
        var results = await RaceAsync(w.Database, 2, (db, i) => i == 0
            ? Quotations(w, db).AcceptAsync(w.Survey.Request.Customer.Id, w.QuotationId, None)
            : Quotations(w, db).RejectAsync(w.Survey.Request.Customer.Id, w.QuotationId, "no", None));
        Assert.Single(results, r => r.Succeeded);
        var status = (await StateAsync(w)).Quotation;
        Assert.Equal(results[0].Succeeded ? QuotationStatuses.Accepted : QuotationStatuses.Rejected, status);
    }

    [PostgresFact]
    public async Task Acceptance_first_then_a_revision_request_waits_on_the_quotation_lock_and_is_refused()
    {
        var w = await AcceptedAsync(accept: false);
        await using var _ = w.Database;
        await using var dbA = w.Database.NewContext();
        await using var dbB = w.Database.NewContext();
        var (held, control) = LockHoldProxy<IQuotationUnitOfWork>.Wrap(new QuotationUnitOfWork(dbA), nameof(IQuotationUnitOfWork.LockAsync));
        var customer = w.Survey.Request.Customer.Id;

        var accepting = Quotations(w, dbA, held).AcceptAsync(customer, w.QuotationId, None);
        await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // the acceptance holds the quotation row lock
        var revising = Quotations(w, dbB).RequestRevisionAsync(customer, w.QuotationId, "Please lower the price", None);
        await WaitUntilBlockedOnLockAsync(w.Database);
        control.Release.SetResult();
        var (accepted, revised) = (await accepting, await revising);

        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Equal(QuotationErrorCodes.NotIssued, revised.ErrorCode);
        var stored = await QuotationRowAsync(w);
        Assert.Equal((QuotationStatuses.Accepted, true, (DateTimeOffset?)null, (string?)null), (stored.Status, stored.AcceptedAt is not null, stored.RevisionRequestedAt, stored.RevisionReason));
        Assert.Equal([QuotationEventTypes.Accepted], await OutcomeEventsAsync(w));
    }

    [PostgresFact]
    public async Task A_revision_request_first_then_acceptance_waits_on_the_quotation_lock_and_is_refused()
    {
        var w = await AcceptedAsync(accept: false);
        await using var _ = w.Database;
        await using var dbA = w.Database.NewContext();
        await using var dbB = w.Database.NewContext();
        var (held, control) = LockHoldProxy<IQuotationUnitOfWork>.Wrap(new QuotationUnitOfWork(dbA), nameof(IQuotationUnitOfWork.LockAsync));
        var customer = w.Survey.Request.Customer.Id;

        var revising = Quotations(w, dbA, held).RequestRevisionAsync(customer, w.QuotationId, "Please lower the price", None);
        await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // the revision request holds the quotation row lock
        var accepting = Quotations(w, dbB).AcceptAsync(customer, w.QuotationId, None);
        await WaitUntilBlockedOnLockAsync(w.Database);
        control.Release.SetResult();
        var (revised, accepted) = (await revising, await accepting);

        Assert.True(revised.Succeeded, revised.Message);
        Assert.Equal(QuotationErrorCodes.NotIssued, accepted.ErrorCode);
        var stored = await QuotationRowAsync(w);
        Assert.Equal((QuotationStatuses.RevisionRequested, (DateTimeOffset?)null, "Please lower the price"), (stored.Status, stored.AcceptedAt, stored.RevisionReason));
        Assert.Equal([QuotationEventTypes.RevisionRequested], await OutcomeEventsAsync(w));
    }

    private static async Task<Quotation> QuotationRowAsync(World w)
    {
        await using var db = w.Database.NewContext();
        return await db.Quotations.AsNoTracking().SingleAsync(q => q.Id == w.QuotationId);
    }

    private static async Task<List<string>> OutcomeEventsAsync(World w)
    {
        await using var db = w.Database.NewContext();
        return await db.QuotationEvents.AsNoTracking()
            .Where(e => e.QuotationId == w.QuotationId && (e.EventType == QuotationEventTypes.Accepted || e.EventType == QuotationEventTypes.RevisionRequested))
            .Select(e => e.EventType).ToListAsync();
    }

    [PostgresFact]
    public async Task Duplicate_closures_racing_close_once_and_record_one_event()
    {
        var w = await AcceptedAsync(accept: false);
        await using var _ = w.Database;
        var results = await RaceAsync(w.Database, 4, (db, _) =>
            Quotations(w, db).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "NO_RESPONSE", null, None));
        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));   // same outcome: idempotent
        await using var db = w.Database.NewContext();
        Assert.Single(await db.SurveyRequestEvents.AsNoTracking()
            .Where(e => e.SurveyRequestId == w.Survey.Request.Request.Id && e.EventType == SmartSolar.Modules.PreSurvey.Entities.SurveyRequestEventTypes.RequestClosed).ToListAsync());
        Assert.Single(await db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == w.QuotationId && e.EventType == QuotationEventTypes.Cancelled).ToListAsync());
    }

    [PostgresFact]
    public async Task A_revision_racing_a_closure_never_leaves_an_open_quotation_on_a_closed_request()
    {
        var w = await AcceptedAsync(accept: false);
        await using var _ = w.Database;
        Assert.True((await w.Ctx.Quotations().RejectAsync(w.Survey.Request.Customer.Id, w.QuotationId, "Too expensive", None)).Succeeded);
        var results = await RaceAsync(w.Database, 2, async (db, i) => i == 0
            ? (await Quotations(w, db).CreateRevisionAsync(w.Survey.Sales.Id, w.QuotationId, None)).Succeeded
            : (await Quotations(w, db).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).Succeeded);
        Assert.True(results[1]);
        await using var db = w.Database.NewContext();
        Assert.Equal(SurveyRequestStatus.Closed, (await db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == w.Survey.Request.Request.Id)).Status);
        Assert.All(await db.Quotations.AsNoTracking().Where(q => q.SurveyRequestId == w.Survey.Request.Request.Id).ToListAsync(),
            q => Assert.True(QuotationStatuses.IsTerminal(q.Status), $"{q.Version}: {q.Status}"));
    }

    // ------------------------------------------------------------------ M-7 contract draft discard

    private static async Task<(World W, ContractView Draft)> DraftAsync()
    {
        var w = await AcceptedAsync();
        var draft = (await w.Ctx.Contracts().CreateAsync(w.Survey.Sales.Id, w.QuotationId, "Terms", None)).Value!;
        return (w, draft);
    }

    private static async Task<SmartSolar.Modules.CustomerContracts.CustomerContract> ContractRowAsync(World w, Guid contractId)
    {
        await using var db = w.Database.NewContext();
        return await db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contractId);
    }

    private static async Task<int> DiscardEventsAsync(World w, Guid contractId)
    {
        await using var db = w.Database.NewContext();
        return await db.ContractEvents.AsNoTracking().CountAsync(e => e.ContractId == contractId && e.ToStatus == ContractStatuses.Discarded);
    }

    [PostgresFact]
    public async Task Discard_and_issue_racing_on_the_same_draft_have_exactly_one_consistent_winner()
    {
        var (w, draft) = await DraftAsync();
        await using var _ = w.Database;
        var results = await RaceAsync(w.Database, 2, (db, i) => i == 0
            ? Contracts(w, db).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, "Customer withdrew", None)
            : Contracts(w, db).IssueAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, None));
        Assert.Single(results, r => r.Succeeded);
        var row = await ContractRowAsync(w, draft.ContractId);
        if (results[0].Succeeded)
            Assert.Equal((ContractStatuses.Discarded, (DateTimeOffset?)null, (string?)null, 1), (row.Status, row.IssuedAt, row.CanonicalHash, await DiscardEventsAsync(w, draft.ContractId)));
        else
            Assert.Equal((ContractStatuses.Issued, (DateTimeOffset?)null, (string?)null, 0), (row.Status, row.DiscardedAt, row.DiscardReason, await DiscardEventsAsync(w, draft.ContractId)));
    }

    [PostgresFact]
    public async Task Concurrent_discards_record_one_discard_and_a_concurrent_terms_edit_never_lands_on_a_discarded_draft()
    {
        var (w, draft) = await DraftAsync();
        await using var _ = w.Database;
        var discards = await RaceAsync(w.Database, 4, (db, _) =>
            Contracts(w, db).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, "Customer withdrew", None));
        Assert.All(discards, r => Assert.True(r.Succeeded, r.Message));   // the winner discards; the others see DISCARDED and change nothing
        Assert.Equal(1, await DiscardEventsAsync(w, draft.ContractId));
        Assert.Equal(ContractStatuses.Discarded, (await ContractRowAsync(w, draft.ContractId)).Status);

        var (w2, draft2) = await DraftAsync();
        await using var __ = w2.Database;
        var raced = await RaceAsync(w2.Database, 2, (db, i) => i == 0
            ? Contracts(w2, db).DiscardAsync(w2.Survey.Sales.Id, draft2.ContractId, draft2.Revision, "Customer withdrew", None)
            : Contracts(w2, db).UpdateTermsAsync(w2.Survey.Sales.Id, draft2.ContractId, draft2.Revision, "Edited terms", None));
        Assert.Single(raced, r => r.Succeeded);
        var row = await ContractRowAsync(w2, draft2.ContractId);
        Assert.Equal(raced[0].Succeeded ? (ContractStatuses.Discarded, "Terms") : (ContractStatuses.Draft, "Edited terms"), (row.Status, row.Terms));
    }

    [PostgresFact]
    public async Task Discard_then_closure_and_closure_then_discard_serialise_on_the_request_lock()
    {
        // Discard holds the request lock and has not committed: a closure now still sees the live DRAFT and refuses at once (it never
        // closes past it); once the discard commits, the explicit closure succeeds.
        var (w, draft) = await DraftAsync();
        await using (w.Database)
        {
            await using var dbA = w.Database.NewContext();
            await using var dbB = w.Database.NewContext();
            var (held, control) = LockHoldProxy<IContractUnitOfWork>.Wrap(new ContractUnitOfWork(dbA));
            var discarding = Contracts(w, dbA, held).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, "Customer withdrew", None);
            await control.Locked.Task.WaitAsync(TimeSpan.FromSeconds(30));   // fail, never hang, if the lock is not reached
            Assert.Equal(SalesOutcomeErrorCodes.ContractExists,
                (await Quotations(w, dbB).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).ErrorCode);
            control.Release.SetResult();
            Assert.True((await discarding).Succeeded);
            Assert.True((await Quotations(w, dbB).CloseRequestAsync(w.Survey.Sales.Id, w.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).Succeeded);
            var s = await StateAsync(w);
            Assert.Equal((SurveyRequestStatus.Closed, QuotationStatuses.Cancelled, ContractStatuses.Discarded), (s.Request, s.Quotation, Assert.Single(s.Contracts)));
        }

        // A closure while the DRAFT is live refuses on its pre-check without taking the request lock (so it can never hold up, or close past,
        // a discard); the discard then completes and the explicit closure follows.
        var (w2, draft2) = await DraftAsync();
        await using (w2.Database)
        {
            await using var dbA = w2.Database.NewContext();
            await using var dbB = w2.Database.NewContext();
            var (held, control) = LockHoldProxy<IQuotationUnitOfWork>.Wrap(new QuotationUnitOfWork(dbA));
            control.Release.SetResult();   // never hold: this closure must not even reach the lock
            var closing = await Quotations(w2, dbA, held).CloseRequestAsync(w2.Survey.Sales.Id, w2.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None);
            Assert.Equal(SalesOutcomeErrorCodes.ContractExists, closing.ErrorCode);
            Assert.False(control.Locked.Task.IsCompleted);
            Assert.True((await Contracts(w2, dbB).DiscardAsync(w2.Survey.Sales.Id, draft2.ContractId, draft2.Revision, "Customer withdrew", None)).Succeeded);
            var s = await StateAsync(w2);
            Assert.Equal((SurveyRequestStatus.Completed, QuotationStatuses.Accepted, ContractStatuses.Discarded), (s.Request, s.Quotation, Assert.Single(s.Contracts)));
            // The explicit second step now succeeds.
            Assert.True((await Quotations(w2, dbB).CloseRequestAsync(w2.Survey.Sales.Id, w2.Survey.Request.Request.Id, "CUSTOMER_DECLINED", null, None)).Succeeded);
        }
    }

    [PostgresFact]
    public async Task A_stale_discard_is_retried_with_the_fresh_revision_without_duplicate_events()
    {
        var (w, draft) = await DraftAsync();
        await using var _ = w.Database;
        await using var db = w.Database.NewContext();
        var edited = (await Contracts(w, db).UpdateTermsAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, "Edited", None)).Value!;
        Assert.Equal(ContractErrorCodes.StaleRevision, (await Contracts(w, db).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, draft.Revision, "x", None)).ErrorCode);
        Assert.True((await Contracts(w, db).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, edited.Revision, "Customer withdrew", None)).Succeeded);
        Assert.True((await Contracts(w, db).DiscardAsync(w.Survey.Sales.Id, draft.ContractId, edited.Revision, "Customer withdrew", None)).Succeeded);
        Assert.Equal(1, await DiscardEventsAsync(w, draft.ContractId));
    }
}

