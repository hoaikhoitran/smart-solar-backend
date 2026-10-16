using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
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
/// caller has its own DbContext/connection. PayOS, storage and travel are synthetic.
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

    [PostgresFact]
    public async Task Two_managers_booking_the_same_technician_on_overlapping_shifts_cannot_both_succeed()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var first = await InstallationTests.PaidContractAsync(ctx);
        ctx.Clock.Now = DefaultNow.ToUniversalTime();   // the first on-site survey moved the clock past the 10:00 slot
        var second = await InstallationTests.PaidContractAsync(ctx);
        var shared = await ctx.Scheduling.SeedTechnicianAsync("Shared tech", District1);
        var p1 = (await ctx.Installations().CreateProjectAsync(first.ManagerId, first.ContractId, None)).Value!;
        var p2 = (await ctx.Installations().CreateProjectAsync(second.ManagerId, second.ContractId, None)).Value!;
        var wed = Day.AddDays(1);
        IReadOnlyList<ShiftInput> shifts = [new ShiftInput(wed, new TimeOnly(8, 0), new TimeOnly(12, 0))];

        var results = await RaceAsync(database, 2, (db, i) => i == 0
            ? ctx.Installations(db).ConfirmScheduleAsync(first.ManagerId, p1.ProjectId, p1.Revision, [shared.Id], shared.Id, shifts, None)
            : ctx.Installations(db).ConfirmScheduleAsync(second.ManagerId, p2.ProjectId, p2.Revision, [shared.Id], shared.Id, shifts, None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(InstallationErrorCodes.ScheduleConflict, Assert.Single(results, r => !r.Succeeded).ErrorCode);
        await using var check = database.NewContext();
        Assert.Equal(1, await check.InstallationShiftAssignments.CountAsync(a => a.TechnicianUserId == shared.Id && a.Status == "ACTIVE"));
    }

    [PostgresFact]
    public async Task The_exclusion_constraint_rejects_overlapping_assignments_written_directly()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (_, project, leader, _) = await InstallationTests.ScheduledAsync(ctx);
        await using var db = database.NewContext();
        var existing = await db.InstallationShiftAssignments.AsNoTracking().FirstAsync(a => a.TechnicianUserId == leader && a.Status == "ACTIVE");
        db.InstallationShiftAssignments.Add(new InstallationShiftAssignment
        {
            Id = Guid.NewGuid(), ShiftId = existing.ShiftId, ProjectId = project.ProjectId, TechnicianUserId = leader,
            StartsAt = existing.StartsAt.AddMinutes(30), EndsAt = existing.EndsAt.AddMinutes(30), Status = "ACTIVE", CreatedAt = DateTimeOffset.UtcNow,
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(((PostgresException)error.InnerException!).SqlState, new[] { PostgresErrorCodes.ExclusionViolation, PostgresErrorCodes.UniqueViolation });
    }

    [PostgresFact]
    public async Task Disabling_a_technician_races_an_installation_booking_without_leaving_an_inactive_booked_technician()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Racing tech", District1);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        IReadOnlyList<ShiftInput> shifts = [new ShiftInput(Day.AddDays(1), new TimeOnly(8, 0), new TimeOnly(12, 0))];

        var results = await RaceAsync(database, 2, async (db, i) =>
        {
            if (i == 0)
                return (await ctx.Installations(db).ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [tech.Id], tech.Id, shifts, None)).Succeeded;
            var u = ctx.SchedulingWithInstallations(db);
            var admin = new AdminTechnicianHandlers(u, ctx.Scheduling.Recalculation(u), ctx.Scheduling.Calendar, ctx.Clock);
            return (await admin.UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech.Id, District1.Latitude, District1.Longitude, null, false, null), None)).Succeeded;
        });

        await using var check = database.NewContext();
        var active = (await check.TechnicianProfiles.SingleAsync(p => p.UserId == tech.Id)).IsSchedulingActive;
        var booked = await check.InstallationShiftAssignments.AnyAsync(a => a.TechnicianUserId == tech.Id && a.Status == "ACTIVE");
        Assert.Equal(1, results.Count(r => r));
        Assert.True(active == booked, $"active={active}, booked={booked}");
    }

    [PostgresFact]
    public async Task Double_milestone_completion_completes_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (_, project, leader, _) = await InstallationTests.ScheduledAsync(ctx);
        ctx.Clock.Now = At(8, 30, Day.AddDays(1)).ToUniversalTime();
        Assert.True((await ctx.Installations().StartProjectAsync(leader, project.ProjectId, None)).Succeeded);
        var milestone = project.Milestones[0];
        await ctx.Installations().StartMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, None);
        var photo = await ctx.VerifiedFileAsync(leader, FilePurposes.InstallationEvidence);
        Assert.True((await ctx.Installations().AddEvidenceAsync(leader, project.ProjectId, milestone.MilestoneId, photo, null, None)).Succeeded);

        var results = await RaceAsync(database, 5, (db, _) => ctx.Installations(db).CompleteMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, None));

        Assert.All(results, r => Assert.True(r.Succeeded || r.ErrorCode == InstallationErrorCodes.StaleRevision, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(MilestoneStatuses.Completed, (await check.InstallationMilestones.SingleAsync(m => m.Id == milestone.MilestoneId)).Status);
        Assert.Equal(1, await check.InstallationEvents.CountAsync(e => e.ProjectId == project.ProjectId && e.EventType == "MILESTONE_COMPLETED"));
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

    [PostgresFact]
    public async Task A_survey_booking_racing_an_installation_confirmation_never_double_books_the_technician()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Contended", District1);
        await using (var db = database.NewContext())
            await db.TechnicianProfiles.Where(p => p.UserId != tech.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, false));
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Racing customer");
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);
        IReadOnlyList<ShiftInput> shifts = [new ShiftInput(wed, new TimeOnly(8, 0), new TimeOnly(12, 0))];

        SmartSolar.Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentHandler Book(AppDbContext db)
        {
            var u = ctx.SchedulingWithInstallations(db);
            var evaluator = new SmartSolar.Modules.Scheduling.Services.TechnicianAvailabilityEvaluator(ctx.Scheduling.Options, ctx.Clock);
            return new(u, new SmartSolar.Modules.Scheduling.Services.AppointmentReservationService(u, evaluator, ctx.Scheduling.Travel, ctx.Scheduling.Options, ctx.Clock,
                ctx.Scheduling.Calendar), ctx.Scheduling.Calendar, ctx.Clock);
        }

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await ctx.Installations(db).ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [tech.Id], tech.Id, shifts, None)).Succeeded
            : (await Book(db).HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10, 0, wed), null), None)).Succeeded);

        await using var check = database.NewContext();
        var surveys = await check.TechnicianAppointments.CountAsync(a => a.ReservedTechnicianUserId == tech.Id && a.Status != SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Cancelled);
        var installs = await check.InstallationShiftAssignments.CountAsync(a => a.TechnicianUserId == tech.Id && a.Status == "ACTIVE");
        Assert.Equal(1, results.Count(r => r));
        Assert.True(surveys + installs == 1, $"surveys={surveys}, installation assignments={installs}");
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
