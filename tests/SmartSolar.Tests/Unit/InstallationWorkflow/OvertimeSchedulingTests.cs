using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Overtime protection (2026-11-25 hardening) on the production handlers (SQLite): work in execution past its reserved end keeps occupying the technician
/// until the end of that local day; nothing becomes free merely because EndsAt passed; existing commitments collided by the overrun go to Manager/Admin
/// dispatch exactly once and are never changed automatically. OT numbers refer to guides/scheduling-early-completion/TEST_REPORT.md.
/// </summary>
public sealed class OvertimeSchedulingTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);
    private static readonly DateOnly Thu = Day.AddDays(2);
    private static readonly DateOnly Fri = Day.AddDays(3);

    private static Task<string?> ReasonAsync(InstallationWorkflowTestContext ctx, Guid technician, DateTimeOffset start, DateTimeOffset end)
        => ctx.Scheduling.Eligibility().CheckAsync(technician, new EligibilityRequest(start.ToUniversalTime(), end.ToUniversalTime(), null), None)
            .ContinueWith(t => t.Result.Reason);

    internal static OvertimeConflictService Conflicts(InstallationWorkflowTestContext ctx)
    {
        var u = ctx.Scheduling.UnitOfWork;
        return new OvertimeConflictService(u, ctx.Scheduling.Eligibility(u), ctx.Scheduling.Calendar, ctx.Clock);
    }

    // ================================================================== surveys (A1)

    [Fact]
    public async Task OT01_a_survey_in_progress_past_its_end_keeps_occupying_the_technician_for_the_rest_of_the_day()
    {
        var (ctx, a) = await EarlyCompletionSchedulingTests.SurveyAsync();
        await using var _ = ctx;
        await EarlyCompletionSchedulingTests.StartAsync(ctx, a, At(10));   // report IN_PROGRESS: authoritative execution evidence
        // Within the reservation nothing changes: 13:00 is free.
        ctx.Clock.Now = At(10, 30).ToUniversalTime();
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));
        // 12:10, still on site after the 11:30 end: the rest of the day is held; the reservation itself is unchanged.
        ctx.Clock.Now = At(12, 10).ToUniversalTime();
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(15, 30), At(17)));
        Assert.False((await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(13))).Succeeded);
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(8, 0, Wed), At(9, 30, Wed)));   // the next day is not held
        var row = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
        Assert.Equal((At(10).ToUniversalTime(), At(11, 30).ToUniversalTime(), AppointmentStatuses.Accepted), (row.StartsAt.ToUniversalTime(), row.EndsAt.ToUniversalTime(), row.Status));
    }

    [Fact]
    public async Task OT02_an_accepted_survey_that_never_started_is_not_treated_as_in_progress_after_its_end()
    {
        var (ctx, a) = await EarlyCompletionSchedulingTests.SurveyAsync();
        await using var _ = ctx;
        ctx.Clock.Now = At(12, 10).ToUniversalTime();   // ACCEPTED, start and end passed, no on-site start recorded
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));
        Assert.Empty((await ctx.Scheduling.Eligibility().LoadDaysAsync(Day, new TechnicianScope([a.Technician.Id], SchedulableOnly: false), None))
            .Single().Appointments.Where(s => s.InExecution));
    }

    [Fact]
    public async Task OT03_an_overrunning_survey_releases_at_its_confirmed_completion_plus_the_buffer()
    {
        var (ctx, a) = await EarlyCompletionSchedulingTests.SurveyAsync();
        await using var _ = ctx;
        var buffer = (await ctx.Scheduling.Policies.GetAsync(None)).BufferBetweenAppointmentsMinutes;
        await EarlyCompletionSchedulingTests.CompleteAsync(ctx, a, At(10), At(12, 20));
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));   // 12:20 + buffer ≤ 13:00
        var stop = (await ctx.Scheduling.Eligibility().LoadDaysAsync(Day, new TechnicianScope([a.Technician.Id], SchedulableOnly: false), None)).Single()
            .Appointments.Single(s => s.AppointmentId == a.AppointmentId);
        Assert.Equal((At(10).ToUniversalTime(), At(12, 20).ToUniversalTime(), false), (stop.StartsAtUtc.ToUniversalTime(), stop.EndsAtUtc.ToUniversalTime(), stop.InExecution));
        Assert.True(EligibilityRules.Conflicts(stop, At(12, 20).AddMinutes(buffer - 5), At(14), TimeSpan.FromMinutes(buffer)));
    }

    // ================================================================== installations (A2)

    [Fact]
    public async Task OT04_an_installation_in_progress_past_its_end_keeps_occupying_until_its_completion_report()
    {
        var i = await EarlyCompletionSchedulingTests.InProgressAsync();
        await using var _ = i.Ctx;
        // Reserved Wed 08:00 – Thu 12:00; still IN_PROGRESS on Thursday 13:30.
        i.Ctx.Clock.Now = At(13, 30, Thu).ToUniversalTime();
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(i.Ctx, i.TechnicianId, At(14, 0, Thu), At(15, 30, Thu)));
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(8, 0, Fri), At(9, 30, Fri)));
        // The report releases at the report time (+ buffer); the customer still decides and the balance stays closed.
        var reported = await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Done late", null, None);
        Assert.True(reported.Succeeded, reported.Message);
        var buffer = (await i.Ctx.Scheduling.Policies.GetAsync(None)).BufferBetweenAppointmentsMinutes;
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(13, 30, Thu).AddMinutes(buffer), At(16, 0, Thu)));
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, reported.Value!.Status);
        Assert.Equal(Modules.CustomerContracts.InstallmentStatuses.Pending, (await PaymentPlanFlow.InstallmentAsync(i.Ctx, i.Flow.ContractId, 2)).Status);
    }

    [Fact]
    public async Task OT05_a_scheduled_installation_that_never_started_is_not_in_progress_after_its_end()
    {
        var i = await EarlyCompletionSchedulingTests.InProgressAsync();
        await using var _ = i.Ctx;
        // Synthetic: the same project before its start (SCHEDULED) — only the status is authoritative evidence of execution.
        var project = await i.Ctx.Db.InstallationProjects.SingleAsync(p => p.Id == i.ProjectId);
        project.Status = InstallationStatuses.Scheduled;
        await i.Ctx.Db.SaveChangesAsync();
        i.Ctx.Db.ChangeTracker.Clear();
        i.Ctx.Clock.Now = At(13, 30, Thu).ToUniversalTime();
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(14, 0, Thu), At(15, 30, Thu)));
    }

    [Fact]
    public async Task OT06_rework_occupies_beyond_its_end_only_once_rework_is_actually_performed()
    {
        var i = await EarlyCompletionSchedulingTests.InProgressAsync();
        await using var _ = i.Ctx;
        i.Ctx.Clock.Now = At(11, 0, Wed).ToUniversalTime();
        Assert.True((await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Done", null, None)).Succeeded);
        var photo = await i.Ctx.VerifiedFileAsync(i.Flow.CustomerId, FilePurposes.AcceptanceEvidence);
        Assert.True((await i.Ctx.Installations().DecideAcceptanceAsync(i.Flow.CustomerId, i.ProjectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, "Loose cable", [photo], null), None)).Succeeded);
        await InstallationTests.ReworkScheduledAsync(i.Ctx, i.ProjectId, i.TechnicianId, i.Flow.Manager.Id, daysAhead: 2);   // Fri 08:00–12:00
        // Rework assignment accepted but no rework performed yet: after its end it is not held.
        i.Ctx.Clock.Now = At(13, 0, Fri).ToUniversalTime();
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(14, 0, Fri), At(15, 30, Fri)));
        // Evidence of the rework round = rework in execution → held for the rest of the day.
        await InstallationTests.AddCompletionEvidenceAsync(i.Ctx, i.ProjectId, i.TechnicianId);
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(i.Ctx, i.TechnicianId, At(14, 0, Fri), At(15, 30, Fri)));
    }

    // ================================================================== existing commitments (A3)

    /// <summary>Survey A 10:00–11:30 in progress; survey B of another customer booked 13:00 and accepted by the same technician; a Manager exists.</summary>
    internal static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey A, Guid B, Guid ManagerId)> OverrunWithBookingAsync(InstallationWorkflowTestContext? on = null)
    {
        var (ctx, a) = await EarlyCompletionSchedulingTests.SurveyAsync(on);
        var manager = await ctx.Scheduling.SeedStaffAsync("Dispatch manager", RoleCodes.Manager);
        var booked = await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer B", At(13));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");
        Assert.True((await ctx.Scheduling.AcceptAsync(a.Technician.Id, booked.Value!.AppointmentId)).Succeeded);
        await EarlyCompletionSchedulingTests.StartAsync(ctx, a, At(10));
        ctx.Clock.Now = At(12, 10).ToUniversalTime();
        return (ctx, a, booked.Value.AppointmentId, manager.Id);
    }

    [Fact]
    public async Task OT07_an_overrun_into_a_confirmed_booking_goes_to_dispatch_once_without_changing_the_booking()
    {
        var (ctx, a, b, managerId) = await OverrunWithBookingAsync();
        await using var _ = ctx;
        var before = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b);

        Assert.Equal(1, await Conflicts(ctx).SweepAsync(50, None));
        Assert.Equal(0, await Conflicts(ctx).SweepAsync(50, None));   // duplicate run: nothing new
        var conflict = await ctx.Db.TechnicianScheduleConflicts.AsNoTracking().SingleAsync();
        Assert.Equal((ScheduleConflictStatuses.Open, a.AppointmentId, b, ScheduleConflictKinds.SurveyAppointment),
            (conflict.Status, conflict.OverrunId, conflict.AffectedId, conflict.AffectedKind));
        Assert.Equal(At(11, 30).ToUniversalTime(), conflict.OverrunReservedEndsAt.ToUniversalTime());
        Assert.Single(await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.AppointmentId == b && e.EventType == AppointmentEventTypes.OvertimeConflict).ToListAsync());
        Assert.Contains(await ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == managerId).Select(n => n.Type).ToListAsync(),
            t => t == AppointmentEventTypes.OvertimeConflict);
        // The customer's booking is preserved exactly (no cancellation, no move, no reassignment).
        var after = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b);
        Assert.Equal((before.Status, before.StartsAt, before.EndsAt, before.AssignedTechnicianUserId, before.Revision),
            (after.Status, after.StartsAt, after.EndsAt, after.AssignedTechnicianUserId, after.Revision));
        // No further commitment can be added that day while the overrun lasts.
        Assert.False((await EarlyCompletionSchedulingTests.BookAsync(ctx, "Customer C", At(15, 30))).Succeeded);
        var listed = await Conflicts(ctx).ListAsync(null, "OPEN", None);
        Assert.Equal(conflict.Id, Assert.Single(listed).ConflictId);

        // A completes at 12:20: the next run resolves the conflict (OVERTIME_ENDED); a later run changes nothing.
        var report = await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.AppointmentId == a.AppointmentId);
        ctx.Clock.Now = At(12, 20).ToUniversalTime();
        Assert.True((await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(a.Technician.Id, a.AppointmentId, report.Revision), None)).Succeeded);
        Assert.Equal(1, await Conflicts(ctx).SweepAsync(50, None));
        Assert.Equal(0, await Conflicts(ctx).SweepAsync(50, None));
        var resolved = await ctx.Db.TechnicianScheduleConflicts.AsNoTracking().SingleAsync();
        Assert.Equal((ScheduleConflictStatuses.Resolved, ScheduleConflictResolutions.OvertimeEnded), (resolved.Status, resolved.Resolution));
        Assert.Equal(AppointmentStatuses.Accepted, (await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == b)).Status);
    }

    [Fact]
    public async Task OT08_no_conflict_is_opened_while_the_work_is_within_its_reservation_or_when_nothing_collides()
    {
        var (ctx, a, _, _) = await OverrunWithBookingAsync();
        await using var __ = ctx;
        ctx.Clock.Now = At(11, 0).ToUniversalTime();   // in progress but within 10:00–11:30
        Assert.Equal(0, await Conflicts(ctx).SweepAsync(50, None));
        Assert.Empty(await ctx.Db.TechnicianScheduleConflicts.AsNoTracking().ToListAsync());
    }
}
