using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Real PostgreSQL behaviour of the Installation Flow under concurrency, on a disposable local database
/// created from the PENDING_MANUAL_MIGRATION review script (never a shared database). Each racing
/// caller has its own DbContext/connection. PayOS and storage are synthetic.
/// </summary>
public sealed class PostgresInstallationWorkflowConcurrencyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Two_sales_editing_one_draft_quotation_cannot_both_win()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (survey, issued) = await QuotationTests.IssuedAsync(ctx);
        var draft = (await ctx.Quotations().CreateRevisionAsync(survey.Sales.Id, issued.QuotationId, None)).Value!;
        var product = await ctx.ProductAsync("PG-PNL", 3_000_000m);

        var results = await RaceAsync(database, 4, (db, i) => ctx.Quotations(db).AddLineAsync(survey.Sales.Id, draft.QuotationId, draft.Revision,
            new QuotationLineInput(product.Id, null, null, i + 1, null, null), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(QuotationErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        var lines = await check.QuotationLines.CountAsync(l => l.QuotationId == draft.QuotationId);
        Assert.Equal(issued.Lines.Count + 1, lines);
    }

    [PostgresFact]
    public async Task Acceptance_and_revision_request_race_to_exactly_one_outcome()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (survey, issued) = await QuotationTests.IssuedAsync(ctx);
        var customer = survey.Request.Customer.Id;

        var results = await RaceAsync(database, 6, (db, i) => i % 2 == 0
            ? ctx.Quotations(db).AcceptAsync(customer, issued.QuotationId, None)
            : ctx.Quotations(db).RequestRevisionAsync(customer, issued.QuotationId, "Please lower the price", None));

        await using var check = database.NewContext();
        var status = (await check.Quotations.SingleAsync(q => q.Id == issued.QuotationId)).Status;
        Assert.Contains(status, new[] { QuotationStatuses.Accepted, QuotationStatuses.RevisionRequested });
        // Every success reports the same final state (repeats of the winner are idempotent).
        Assert.All(results.Where(r => r.Succeeded), r => Assert.Equal(status, r.Value!.Status));
        Assert.Equal(1, await check.QuotationEvents.CountAsync(e => e.QuotationId == issued.QuotationId && (e.EventType == "ACCEPTED" || e.EventType == "REVISION_REQUESTED")));
    }

    [PostgresFact]
    public async Task Double_signature_approval_applies_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var ready = await ContractTests.AcceptedQuotationAsync(ctx);
        var published = await ContractTests.PublishedAsync(ctx, ready);
        var submitted = await ContractTests.SubmitSignedAsync(ctx, ready, published);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");

        var results = await RaceAsync(database, 4, (db, i) => i == 0
            ? ctx.Contracts(db).ReviewAsync(ready.Survey.Sales.Id, false, submitted.ContractId, submitted.Revision, ReviewDecision.Approve, null, None)
            : ctx.Contracts(db).ReviewAsync(manager.Id, true, submitted.ContractId, submitted.Revision, ReviewDecision.Approve, null, None));

        Assert.Single(results, r => r.Succeeded);
        await using var check = database.NewContext();
        Assert.Equal(ContractStatuses.Signed, (await check.CustomerContracts.SingleAsync(c => c.Id == submitted.ContractId)).Status);
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == submitted.ContractId && e.EventType == "STATUS_CHANGED" && e.ToStatus == ContractStatuses.Signed));
    }

    [PostgresFact]
    public async Task Duplicate_webhooks_and_reconciliation_confirm_the_payment_exactly_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (ready, contract) = await ContractTests.SignedAsync(ctx);
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(ready.Survey.Request.Customer.Id, contract.ContractId, None)).Value!;
        var webhook = ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, $"FT-{attempt.OrderCode}");
        ctx.PayOs.Links[attempt.OrderCode] = ("PAID", (long)attempt.Amount, $"FT-{attempt.OrderCode}");

        // Six identical webhook deliveries race two reconciliation sweeps.
        var results = await RaceAsync(database, 8, async (db, i) =>
        {
            if (i < 6) return (await ctx.Payments(db).HandleWebhookAsync(webhook, None)).Outcome;
            return (await ctx.Payments(db).ReconcileAsync(attempt.PaymentAttemptId, force: true, None))?.Status ?? "NONE";
        });

        await using var check = database.NewContext();
        Assert.Equal(PaymentStatuses.Paid, (await check.PaymentAttempts.SingleAsync(a => a.Id == attempt.PaymentAttemptId)).Status);
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync(c => c.Id == contract.ContractId)).Status);
        Assert.Equal(1, await check.PaymentEvents.CountAsync(e => e.PaymentAttemptId == attempt.PaymentAttemptId && e.EventType == "PAID"));
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == contract.ContractId && e.EventType == "STATUS_CHANGED" && e.ToStatus == ContractStatuses.Paid));
        Assert.Equal(1, await check.PaymentWebhookEvents.CountAsync());
        Assert.DoesNotContain("REJECTED", results);
    }

    private static AssignTechnicianInput Assign(Guid technician, DateTimeOffset start, DateTimeOffset end, int revision, string? reason = null)
        => new(technician, start, end, reason, revision);

    /// <summary>Two PAID contracts (Binh Tan sites) with fresh projects and one shared Binh Tan technician.</summary>
    private static async Task<(InstallationTests.Paid First, InstallationStaffView P1, InstallationTests.Paid Second, InstallationStaffView P2, Guid Shared)> TwoProjectsAsync(
        InstallationWorkflowTestContext ctx)
    {
        var first = await InstallationTests.PaidContractAsync(ctx);
        ctx.Clock.Now = DefaultNow.ToUniversalTime();   // the first on-site survey moved the clock past the 10:00 slot
        var second = await InstallationTests.PaidContractAsync(ctx);
        var shared = await ctx.Scheduling.SeedTechnicianAsync("Shared tech", BinhTan);
        var p1 = (await ctx.Installations().CreateProjectAsync(first.ManagerId, first.ContractId, None)).Value!;
        var p2 = (await ctx.Installations().CreateProjectAsync(second.ManagerId, second.ContractId, None)).Value!;
        return (first, p1, second, p2, shared.Id);
    }

    /// <summary>
    /// Both managers pass the optimistic check; under the technician_profile lock the second re-check sees the first
    /// PENDING assignment (busy interval with the buffer), and the exclusion constraint is the backstop.
    /// </summary>
    [PostgresFact]
    public async Task Two_managers_assigning_one_technician_to_overlapping_intervals_cannot_both_succeed()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (first, p1, second, p2, shared) = await TwoProjectsAsync(ctx);
        var wed = Day.AddDays(1);

        var results = await RaceAsync(database, 2, (db, i) => i == 0
            ? ctx.Installations(db).AssignAsync(first.ManagerId, p1.ProjectId, Assign(shared, At(8, 0, wed), At(17, 0, wed), p1.Revision), None)
            : ctx.Installations(db).AssignAsync(second.ManagerId, p2.ProjectId, Assign(shared, At(13, 0, wed), At(17, 0, wed), p2.Revision), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(SmartSolar.Modules.Scheduling.Domain.EligibilityReasons.ScheduleConflict, Assert.Single(results, r => !r.Succeeded).ErrorCode);
        await using var check = database.NewContext();
        Assert.Equal(1, await check.InstallationAssignments.CountAsync(a => a.TechnicianUserId == shared && a.Status == AssignmentStatuses.Pending));
        Assert.Equal(1, await check.InstallationProjects.CountAsync(p => p.Status == InstallationStatuses.PendingTechnicianAcceptance));
    }

    /// <summary>Two managers (re)assigning the same project from the same revision: exactly one wins, one current assignment remains.</summary>
    [PostgresFact]
    public async Task Concurrent_assignments_of_one_project_leave_exactly_one_current_assignment()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var a = await ctx.Scheduling.SeedTechnicianAsync("Tech A", BinhTan);
        var b = await ctx.Scheduling.SeedTechnicianAsync("Tech B", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);

        var results = await RaceAsync(database, 4, (db, i) => ctx.Installations(db).AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(i % 2 == 0 ? a.Id : b.Id, At(8, 0, wed), At(12, 0, wed), project.Revision), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(InstallationErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.InstallationAssignments.CountAsync(x => x.ProjectId == project.ProjectId));
        Assert.Equal(project.Revision + 1, (await check.InstallationProjects.SingleAsync(p => p.Id == project.ProjectId)).Revision);
    }

    [PostgresFact]
    public async Task The_exclusion_constraint_rejects_overlapping_current_assignments_written_directly()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (first, p1, _, p2, shared) = await TwoProjectsAsync(ctx);
        var wed = Day.AddDays(1);
        Assert.True((await ctx.Installations().AssignAsync(first.ManagerId, p1.ProjectId, Assign(shared, At(8, 0, wed), At(12, 0, wed), p1.Revision), None)).Succeeded);
        await using var db = database.NewContext();
        db.InstallationAssignments.Add(new InstallationAssignment
        {
            Id = Guid.NewGuid(), ProjectId = p2.ProjectId, TechnicianUserId = shared, StartsAt = At(11, 0, wed).ToUniversalTime(), EndsAt = At(15, 0, wed).ToUniversalTime(),
            Status = AssignmentStatuses.Accepted, AssignedByUserId = first.ManagerId, AssignedAt = DateTimeOffset.UtcNow, RespondedAt = DateTimeOffset.UtcNow,
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ((PostgresException)error.InnerException!).SqlState);
        Assert.Equal("ex_installation_assignment_no_overlap", ((PostgresException)error.InnerException!).ConstraintName);
    }

    [PostgresFact]
    public async Task Disabling_a_technician_races_an_installation_assignment_without_leaving_an_inactive_assigned_technician()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Racing tech", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);

        var results = await RaceAsync(database, 2, async (db, i) =>
        {
            if (i == 0)
                return (await ctx.Installations(db).AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, wed), At(12, 0, wed), project.Revision), None)).Succeeded;
            var u = ctx.SchedulingWithInstallations(db);
            var admin = new AdminTechnicianHandlers(u, ctx.Scheduling.Assignments(u), ctx.Scheduling.Calendar, ctx.Scheduling.Options, ctx.Clock);
            return (await admin.UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech.Id, false, null), None)).Succeeded;
        });

        await using var check = database.NewContext();
        var active = (await check.TechnicianProfiles.SingleAsync(p => p.UserId == tech.Id)).IsSchedulingActive;
        var assigned = await check.InstallationAssignments.AnyAsync(a => a.TechnicianUserId == tech.Id && a.Status == AssignmentStatuses.Pending);
        Assert.Equal(1, results.Count(r => r));
        Assert.True(active == assigned, $"active={active}, assigned={assigned}");
    }

    /// <summary>A survey booking (holder choice under the lock) racing an installation assignment of the only technician: one commitment.</summary>
    [PostgresFact]
    public async Task A_survey_booking_racing_an_installation_assignment_never_double_books_the_technician()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Contended", BinhTan);
        await using (var db = database.NewContext())
            await db.TechnicianProfiles.Where(p => p.UserId != tech.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, false));
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Racing customer");
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);

        SmartSolar.Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentHandler Book(AppDbContext db)
        {
            var u = ctx.SchedulingWithInstallations(db);
            return new(u, ctx.Scheduling.Reservations(u), ctx.Scheduling.Eligibility(u), ctx.Scheduling.Coverage(u), ctx.Scheduling.Calendar, ctx.Clock);
        }

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await ctx.Installations(db).AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, wed), At(12, 0, wed), project.Revision), None)).Succeeded
            : (await Book(db).HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10, 0, wed), null), None)).Succeeded);

        await using var check = database.NewContext();
        var surveys = await check.TechnicianAppointments.CountAsync(a => a.ReservedTechnicianUserId == tech.Id && a.StartsAt > At(0, 0, wed).ToUniversalTime()
            && a.Status != SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Cancelled);
        var installs = await check.InstallationAssignments.CountAsync(a => a.TechnicianUserId == tech.Id && a.Status == AssignmentStatuses.Pending);
        Assert.Equal(1, results.Count(r => r));
        Assert.True(surveys + installs == 1, $"surveys={surveys}, installation assignments={installs}");
    }

    /// <summary>
    /// Survey self-acceptance by technician X (locks X and the holder) racing a Manager assigning X to an overlapping installation
    /// (locks X): both take X's technician_profile lock, so the second one re-checks against the first one's commitment.
    /// </summary>
    [PostgresFact]
    public async Task Survey_self_acceptance_racing_an_installation_assignment_of_the_same_technician_has_one_winner()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var holder = await ctx.Scheduling.SeedTechnicianAsync("Holder", BinhTan);
        var racer = await ctx.Scheduling.SeedTechnicianAsync("Racer", BinhTan);
        var wed = Day.AddDays(1);
        // Book while only the holder is schedulable, then let the racer compete.
        await using (var db = database.NewContext())
            await db.TechnicianProfiles.Where(p => p.UserId != holder.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, false));
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Open survey");
        var pending = await ctx.Scheduling.BookAsync(request, At(10, 0, wed));
        await using (var db = database.NewContext())
            await db.TechnicianProfiles.Where(p => p.UserId == racer.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, true));
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await ctx.Installations(db).AssignAsync(paid.ManagerId, project.ProjectId, Assign(racer.Id, At(8, 0, wed), At(12, 0, wed), project.Revision), None)).Succeeded
            : (await ctx.JobsWithInstallations(db).AcceptAsync(new AcceptAppointmentCommand(racer.Id, pending.Id, null), None)).Succeeded);

        await using var check = database.NewContext();
        var accepted = await check.TechnicianAppointments.AnyAsync(a => a.Id == pending.Id && a.AssignedTechnicianUserId == racer.Id);
        var installs = await check.InstallationAssignments.AnyAsync(a => a.TechnicianUserId == racer.Id && a.Status == AssignmentStatuses.Pending);
        Assert.Equal(1, results.Count(r => r));
        Assert.True(accepted ^ installs, $"accepted={accepted}, installation={installs}");
    }

    /// <summary>The technician's completion report racing the Manager's reassignment from the same revision: exactly one outcome.</summary>
    [PostgresFact]
    public async Task Completion_report_racing_a_reassignment_has_exactly_one_outcome()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (paid, project, tech, other) = await InstallationTests.ScheduledAsync(ctx);
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var revision = (await ctx.Installations().GetForManagerAsync(project.ProjectId, None)).Value!.Revision;
        var thu = Day.AddDays(2);

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await ctx.Installations(db).ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).ErrorCode
            : (await ctx.Installations(db).AssignAsync(paid.ManagerId, project.ProjectId, Assign(other, At(8, 0, thu), At(12, 0, thu), revision, "Swap"), None)).ErrorCode);

        Assert.Equal(1, results.Count(r => r is null));
        await using var check = database.NewContext();
        var stored = await check.InstallationProjects.SingleAsync(p => p.Id == project.ProjectId);
        var reported = await check.InstallationAcceptances.CountAsync(a => a.ProjectId == project.ProjectId);
        var current = await check.InstallationAssignments.SingleAsync(a => a.ProjectId == project.ProjectId
            && (a.Status == AssignmentStatuses.Pending || a.Status == AssignmentStatuses.Accepted));
        if (results[0] is null)
        {
            Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, stored.Status);
            Assert.Equal(1, reported);
            Assert.Equal(tech, current.TechnicianUserId);
        }
        else
        {
            Assert.Equal(InstallationStatuses.InProgress, stored.Status);
            Assert.Equal(0, reported);
            Assert.Equal(other, current.TechnicianUserId);
        }
        Assert.Equal(revision + 1, stored.Revision);
    }

    [PostgresFact]
    public async Task Double_completion_report_creates_one_acceptance_round()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (_, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);

        var results = await RaceAsync(database, 5, (db, _) => ctx.Installations(db).ReportCompletionAsync(tech, project.ProjectId, "Done", null, None));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, new[] { InstallationErrorCodes.StaleRevision, InstallationErrorCodes.InvalidState }));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.InstallationAcceptances.CountAsync(a => a.ProjectId == project.ProjectId));
        Assert.Equal(1, await check.InstallationEvents.CountAsync(e => e.ProjectId == project.ProjectId && e.EventType == InstallationEventTypes.CompletionReported));
    }

    [PostgresFact]
    public async Task A_customer_paying_two_attempts_never_pays_the_contract_twice()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (ready, contract) = await ContractTests.SignedAsync(ctx);
        var customer = ready.Survey.Request.Customer.Id;
        var first = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        Assert.True((await ctx.Payments().CancelCurrentAsync(customer, contract.ContractId, None)).Succeeded);
        var second = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        Assert.NotEqual(first.PaymentAttemptId, second.PaymentAttemptId);

        // The customer completed both checkouts anyway (the first link was still open in a tab): both webhooks race.
        var webhooks = new[] { ctx.PayOs.Webhook(first.OrderCode, (long)first.Amount, "FT-A"), ctx.PayOs.Webhook(second.OrderCode, (long)second.Amount, "FT-B") };
        await RaceAsync(database, 2, (db, i) => ctx.Payments(db).HandleWebhookAsync(webhooks[i], None));

        await using var check = database.NewContext();
        var attempts = await check.PaymentAttempts.Where(a => a.ContractId == contract.ContractId).ToListAsync();
        Assert.Single(attempts, a => a.Status == PaymentStatuses.Paid);
        Assert.Single(attempts, a => a.Status == PaymentStatuses.RefundRequired);
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.SingleAsync(c => c.Id == contract.ContractId)).Status);
        Assert.Equal(1, await check.ContractEvents.CountAsync(e => e.ContractId == contract.ContractId && e.EventType == "STATUS_CHANGED" && e.ToStatus == ContractStatuses.Paid));

        // Database backstop: a second PAID attempt for the contract is rejected even if code were bypassed.
        var refund = attempts.Single(a => a.Status == PaymentStatuses.RefundRequired);
        var incomplete = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment_attempt SET status = 'PAID' WHERE id = {refund.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, incomplete.SqlState);   // PAID requires paid_at and amount_paid = amount
        var error = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payment_attempt SET status = 'PAID', paid_at = now(), amount_paid = amount WHERE id = {refund.Id}"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_payment_attempt_one_paid", error.ConstraintName);
    }

    /// <summary>
    /// Regression: losers used to get QUOTATION_ACTIVE_EXISTS when PostgreSQL ran the line INSERT before the
    /// revision UPDATE and hit ux_quotation_line_number. Many rounds make that ordering likely.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_line_edits_always_lose_with_a_stale_revision()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (survey, issued) = await QuotationTests.IssuedAsync(ctx);
        var draft = (await ctx.Quotations().CreateRevisionAsync(survey.Sales.Id, issued.QuotationId, None)).Value!;
        var product = await ctx.ProductAsync("PG-LINES", 1_000_000m);
        var revision = draft.Revision;

        for (var round = 0; round < 10; round++)
        {
            var current = revision;
            var results = await RaceAsync(database, 6, (db, i) => ctx.Quotations(db).AddLineAsync(survey.Sales.Id, draft.QuotationId, current,
                new QuotationLineInput(product.Id, null, null, i + 1, null, null), None));
            var winner = Assert.Single(results, r => r.Succeeded);
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(QuotationErrorCodes.StaleRevision, r.ErrorCode));
            revision = winner.Value!.Revision;
        }

        await using var check = database.NewContext();
        var numbers = await check.QuotationLines.Where(l => l.QuotationId == draft.QuotationId).Select(l => l.LineNumber).OrderBy(n => n).ToListAsync();
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }
}
