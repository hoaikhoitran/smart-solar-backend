using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Scheduling workers racing web requests on a disposable PostgreSQL database (2026-11-25 hardening): the overtime dispatch sweep (scheduling worker),
/// the installation-schedule confirmation sweep (assigns technicians) and the overdue sweep, against survey completion, customer booking and customer
/// rework. Real transactions, row locks, revision checks and constraints; every test asserts the final state, never timings.
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresSchedulingWorkerConcurrencyTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Thu = Day.AddDays(2);

    private static InstallationWorkflowTestContext Another(PostgresTestDatabase database, DateTimeOffset now)
    {
        var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        ctx.Clock.Now = now.ToUniversalTime();
        return ctx;
    }

    [PostgresFact]
    public async Task W1_duplicate_overtime_sweeps_record_one_conflict_and_one_event()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (_, a, b, _) = await OvertimeSchedulingTests.OverrunWithBookingAsync(ctx);

        var workers = Enumerable.Range(0, 4).Select(_ => Another(database, At(12, 10))).ToList();
        var results = await Task.WhenAll(workers.Select(w => Task.Run(() => OvertimeSchedulingTests.Conflicts(w).SweepAsync(50, None))));
        foreach (var w in workers) await w.DisposeAsync();

        Assert.Equal(1, results.Sum());
        await using var check = database.NewContext();
        Assert.Equal(1, await check.TechnicianScheduleConflicts.CountAsync(c => c.OverrunId == a.AppointmentId && c.AffectedId == b));
        Assert.Equal(1, await check.TechnicianAppointmentEvents.CountAsync(e => e.AppointmentId == b && e.EventType == AppointmentEventTypes.OvertimeConflict));
        Assert.Equal(AppointmentStatuses.Accepted, (await check.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b)).Status);
    }

    [PostgresFact]
    public async Task W2_survey_completion_racing_the_overtime_sweep_ends_with_no_open_conflict()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (_, a, b, _) = await OvertimeSchedulingTests.OverrunWithBookingAsync(ctx);
        var report = await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.AppointmentId == a.AppointmentId);

        await using var completer = Another(database, At(12, 20));
        await using var worker = Another(database, At(12, 20));
        var outcomes = await Task.WhenAll(
            Task.Run(async () => (await completer.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(a.Technician.Id, a.AppointmentId, report.Revision), None)).Succeeded ? 1 : 0),
            Task.Run(() => OvertimeSchedulingTests.Conflicts(worker).SweepAsync(50, None)));
        Assert.Equal(1, outcomes[0]);

        // Whatever ran first, the next worker run leaves nothing open; at most one row ever existed; B untouched.
        await using (var next = Another(database, At(12, 25))) await OvertimeSchedulingTests.Conflicts(next).SweepAsync(50, None);
        await using var check = database.NewContext();
        Assert.Empty(await check.TechnicianScheduleConflicts.AsNoTracking().Where(c => c.Status == ScheduleConflictStatuses.Open).ToListAsync());
        Assert.True(await check.TechnicianScheduleConflicts.CountAsync() <= 1);
        Assert.Equal(AppointmentStatuses.Accepted, (await check.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b)).Status);
        Assert.Equal(AppointmentStatuses.Completed, (await check.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId)).Status);
    }

    [PostgresFact]
    public async Task W3_a_worker_that_loses_a_unique_conflict_retries_on_the_next_run_without_duplicates()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (_, a, b, _) = await OvertimeSchedulingTests.OverrunWithBookingAsync(ctx);

        // Another writer has inserted the same pair but not committed yet: the sweep blocks on the unique index, then fails when it commits.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO technician_schedule_conflict (id, technician_user_id, overrun_kind, overrun_id, overrun_reserved_ends_at, affected_kind, affected_id,
                affected_starts_at, affected_ends_at, status, detected_at, updated_at, revision)
            VALUES ('{Guid.NewGuid()}', '{a.Technician.Id}', 'SURVEY_APPOINTMENT', '{a.AppointmentId}', now(), 'SURVEY_APPOINTMENT', '{b}', now(), now(), 'OPEN', now(), now(), 0)
            """, connection, tx))
            await insert.ExecuteNonQueryAsync();
        await using var worker = Another(database, At(12, 10));
        var sweep = Task.Run(() => OvertimeSchedulingTests.Conflicts(worker).SweepAsync(50, None));
        await Task.Delay(1500);
        Assert.False(sweep.IsCompleted);   // waiting on the other transaction's row
        await tx.CommitAsync();
        Assert.Equal(0, await sweep);      // the unique violation is caught and logged; nothing half-written

        await using (var retry = Another(database, At(12, 15))) Assert.Equal(0, await OvertimeSchedulingTests.Conflicts(retry).SweepAsync(50, None));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.TechnicianScheduleConflicts.CountAsync());
    }

    /// <summary>
    /// 30/70 contract of technician T: deposit paid, plan Thu 13:00–17:00 PLANNED, paper signed WITHOUT the immediate follow-up (so the confirmation is left
    /// to the installation-schedule worker, as after a missed follow-up). Returns the context, the flow and the plan id.
    /// </summary>
    private static async Task<(PaymentPlanFlow.Agreed Flow, Guid PlanId)> PlannedReadyAsync(InstallationWorkflowTestContext ctx)
    {
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        var planned = await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId,
            InstallationScheduleTests.Plan(At(13, 0, Thu), At(17, 0, Thu), note: "Agreed in person"), None);
        Assert.True(planned.Succeeded, planned.Message);
        var noFollowUp = new PaperSignatureHandlers(new ContractUnitOfWork(ctx.Db), ctx.Files(), ctx.PaymentPlanOptions, ctx.Clock);
        var started = await noFollowUp.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(1, null, null), None);
        Assert.True(started.Succeeded, started.Message);
        var file = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
        Assert.True((await noFollowUp.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, file, None)).Succeeded);
        Assert.True((await noFollowUp.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None)).Succeeded);
        var plan = await ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.ContractId == flow.ContractId);
        Assert.Equal(InstallationScheduleStatuses.Planned, plan.Status);
        // Only T serves the zone from here on.
        foreach (var other in await ctx.Db.TechnicianProfiles.Where(p => p.UserId != flow.TechnicianId).ToListAsync()) other.IsSchedulingActive = false;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return (flow, plan.Id);
    }

    [PostgresFact]
    public async Task W4_the_assignment_worker_never_assigns_into_an_overrun_and_races_the_completion_consistently()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (flow, planId) = await PlannedReadyAsync(ctx);
        // T's survey B on Thursday 10:00–11:30, started on site, still running at 12:10.
        var booked = await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(10, 0, Thu));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");
        Assert.True((await ctx.Scheduling.AcceptAsync(flow.TechnicianId, booked.Value!.AppointmentId)).Succeeded);
        var b = new ConfirmedSurvey(null!, flow.Cart.Survey.Technician, flow.Cart.Survey.Sales, booked.Value.AppointmentId);
        var view = await EarlyCompletionSchedulingTests.StartAsync(ctx, b, At(10, 0, Thu));

        await using var completer = Another(database, At(12, 15, Thu));
        await using var worker = Another(database, At(12, 15, Thu));
        var results = await Task.WhenAll(
            Task.Run(async () => (await completer.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(flow.TechnicianId, b.AppointmentId, view.Revision), None)).Succeeded),
            Task.Run(async () => { await worker.Schedules().SweepAsync(25, None); return true; }));
        Assert.True(results[0]);

        await using var check = database.NewContext();
        var plan = await check.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.Id == planId);
        var assignments = await check.InstallationAssignments.AsNoTracking()
            .Where(x => x.TechnicianUserId == flow.TechnicianId && (x.Status == AssignmentStatuses.Pending || x.Status == AssignmentStatuses.Accepted)).ToListAsync();
        // Either the worker ran during the overrun (refused: plan needs a new date, no assignment) or after the completion (assigned and confirmed).
        if (plan.Status == InstallationScheduleStatuses.Confirmed)
            Assert.Equal(At(13, 0, Thu).ToUniversalTime(), Assert.Single(assignments).StartsAt.ToUniversalTime());
        else
        {
            Assert.Equal(InstallationScheduleStatuses.RescheduleRequired, plan.Status);
            Assert.Empty(assignments);
        }
    }

    [PostgresFact]
    public async Task W5_a_customer_booking_and_the_assignment_worker_competing_for_one_technician_never_both_win()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var (flow, planId) = await PlannedReadyAsync(ctx);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Racer");

        await using var booker = Another(database, At(9, 0, Thu));
        await using var worker = Another(database, At(9, 0, Thu));
        var outcomes = await Task.WhenAll(
            Task.Run(async () => (await booker.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13, 0, Thu), null), None)).Succeeded),
            Task.Run(async () => { await worker.Schedules().SweepAsync(25, None); return true; }));

        await using var check = database.NewContext();
        var plan = await check.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.Id == planId);
        var surveyAt13 = await check.TechnicianAppointments.AsNoTracking()
            .CountAsync(x => x.SurveyRequestId == request.Request.Id && x.ReservedTechnicianUserId == flow.TechnicianId && x.Status != AppointmentStatuses.Cancelled);
        // One technician, one Thursday afternoon: the survey booking and the confirmed installation never coexist.
        Assert.False(plan.Status == InstallationScheduleStatuses.Confirmed && surveyAt13 > 0);
        Assert.Equal(outcomes[0], surveyAt13 == 1);
        Assert.True(plan.Status is InstallationScheduleStatuses.Confirmed or InstallationScheduleStatuses.RescheduleRequired);
    }

    [PostgresFact]
    public async Task W6_customer_rework_racing_the_installation_and_overdue_workers_changes_nothing_else()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = InstallationWorkflowTestContext.CreateOn(database.NewContext());
        var i = await EarlyCompletionSchedulingTests.InProgressAsync(ctx);
        ctx.Clock.Now = At(11, 0, Day.AddDays(1)).ToUniversalTime();
        Assert.True((await ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Done", null, None)).Succeeded);
        var photo = await ctx.VerifiedFileAsync(i.Flow.CustomerId, FilePurposes.AcceptanceEvidence);
        var planBefore = await ctx.Db.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.ContractId == i.Flow.ContractId);

        await using var customer = Another(database, At(15, 0, Day.AddDays(1)));
        await using var scheduleWorker = Another(database, At(15, 0, Day.AddDays(1)));
        await using var overdueWorker = Another(database, At(15, 0, Day.AddDays(1)));
        var results = await Task.WhenAll(
            Task.Run(async () => (await customer.Installations().DecideAcceptanceAsync(i.Flow.CustomerId, i.ProjectId,
                new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, "Cable loose", [photo], null), None)).Succeeded),
            Task.Run(async () => { await scheduleWorker.Schedules().SweepAsync(25, None); return true; }),
            Task.Run(async () => { await overdueWorker.Ledger().SweepOverdueAsync(50, None); return true; }));
        Assert.True(results[0]);

        await using var check = database.NewContext();
        Assert.Equal(InstallationStatuses.ReworkRequired, (await check.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId)).Status);
        Assert.Equal(InstallmentStatuses.Pending, (await check.ContractInstallments.AsNoTracking().SingleAsync(x => x.ContractId == i.Flow.ContractId && x.Sequence == 2)).Status);
        Assert.Equal(0, await check.ContractCollectionCases.CountAsync());
        var planAfter = await check.InstallationScheduleProposals.AsNoTracking().SingleAsync(p => p.ContractId == i.Flow.ContractId);
        Assert.Equal((planBefore.Status, planBefore.Revision), (planAfter.Status, planAfter.Revision));
        Assert.Equal(1, await check.InstallationEvents.CountAsync(e => e.ProjectId == i.ProjectId && e.EventType == InstallationEventTypes.ReworkRequested));
        Assert.Single(await check.InstallationAssignments.AsNoTracking().Where(x => x.ProjectId == i.ProjectId).ToListAsync());
    }
}
