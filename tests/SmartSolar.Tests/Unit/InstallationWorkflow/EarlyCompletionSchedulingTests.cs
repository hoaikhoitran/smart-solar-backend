using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Technician capacity after work finishes earlier than its reserved interval (2026-11-25), on the production handlers (SQLite). Survey appointment
/// 10:00–11:30 on <see cref="Day"/> (working hours 08:00–12:00 / 13:00–17:00, buffer = the configured policy, 30 min by default, lead time 0);
/// installation plan Wed 08:00 – Thu 12:00. TC numbers refer to guides/scheduling-early-completion/TEST_REPORT.md.
/// </summary>
public sealed class EarlyCompletionSchedulingTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);
    private static readonly DateOnly Thu = Day.AddDays(2);

    // ------------------------------------------------------------------ survey helpers

    /// <summary>Survey A booked 10:00–11:30 and accepted by technician T, the only technician of the zone.</summary>
    internal static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey A)> SurveyAsync(InstallationWorkflowTestContext? on = null)
    {
        var ctx = on ?? InstallationWorkflowTestContext.Create();
        return (ctx, await ctx.ConfirmedSurveyAsync("Customer A"));
    }

    internal static async Task<OnSiteSurveyView> StartAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s, DateTimeOffset at)
    {
        ctx.Clock.Now = at.ToUniversalTime();
        var started = await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, None);
        Assert.True(started.Succeeded, started.Message);
        var saved = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Value!.Revision, OnSiteSurveyTests.FullData()), None);
        Assert.True(saved.Succeeded, saved.Message);
        var photo = await ctx.VerifiedFileAsync(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence);
        var attached = await ctx.OnSite().AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, photo, "ROOF", "South side"), None);
        Assert.True(attached.Succeeded, attached.Message);
        return attached.Value!;
    }

    internal static async Task CompleteAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s, DateTimeOffset startAt, DateTimeOffset completeAt)
    {
        var view = await StartAsync(ctx, s, startAt);
        ctx.Clock.Now = completeAt.ToUniversalTime();
        var completed = await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, view.Revision), None);
        Assert.True(completed.Succeeded, completed.Message);
    }

    internal static async Task<SchedulingResult<BookingConfirmation>> BookAsync(InstallationWorkflowTestContext ctx, string customer, DateTimeOffset at)
    {
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, customer);
        return await ctx.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, at, null), None);
    }

    private static async Task<string?> ReasonAsync(InstallationWorkflowTestContext ctx, Guid technician, DateTimeOffset start, DateTimeOffset end, IReadOnlyCollection<Guid>? zones = null)
        => (await ctx.Scheduling.Eligibility().CheckAsync(technician, new EligibilityRequest(start.ToUniversalTime(), end.ToUniversalTime(), zones), None)).Reason;

    private static async Task<int> BufferMinutesAsync(InstallationWorkflowTestContext ctx) => (await ctx.Scheduling.Policies.GetAsync(None)).BufferBetweenAppointmentsMinutes;

    // ================================================================== survey (BR-02, BR-06, BR-07)

    [Fact]
    public async Task TC01_TC05_a_survey_completed_early_releases_the_rest_of_its_interval_and_keeps_its_schedule()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        var before = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
        Assert.Equal((At(10), At(11, 30)), (before.StartsAt.ToOffset(At(10).Offset), before.EndsAt.ToOffset(At(10).Offset)));

        // Early start is allowed by the existing rule (onsiteSurvey.earlyStartMinutes), completion at 09:00 (server clock).
        await CompleteAsync(ctx, a, At(8), At(9));

        var after = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
        Assert.Equal((AppointmentStatuses.Completed, before.StartsAt, before.EndsAt), (after.Status, after.StartsAt, after.EndsAt));   // TC05
        Assert.Equal(At(9).ToUniversalTime(), after.CompletedAt);

        // TC01: the reserved 10:00–11:30 no longer blocks the technician: a new survey at 10:00 goes to T (the only technician of the zone).
        var booked = await BookAsync(ctx, "Customer B", At(10));
        Assert.True(booked.Succeeded, $"{booked.ErrorCode} {booked.Message}");
        Assert.Equal(a.Technician.Id, (await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == booked.Value!.AppointmentId)).ReservedTechnicianUserId);
        Assert.Equal(before.EndsAt, (await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId)).EndsAt);
    }

    [Fact]
    public async Task TC02_a_survey_completed_after_its_end_blocks_until_its_real_completion_plus_the_buffer()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        var buffer = await BufferMinutesAsync(ctx);
        await CompleteAsync(ctx, a, At(10), At(11, 45));
        // Finished 15 min after its 11:30 end: busy until 11:45 (never released early), then the configured buffer.
        ctx.Clock.Now = At(8).ToUniversalTime();   // probe the stored intervals
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(10, 30), At(11, 0)));
        Assert.Equal((At(10), At(11, 45)), EffectiveBusy.Of(At(10), At(11, 30), At(11, 45)));
        Assert.True(EligibilityRules.Conflicts(new ScheduledStop(a.AppointmentId, At(10), At(11, 45)), At(11, 45).AddMinutes(buffer - 1), At(13), TimeSpan.FromMinutes(buffer)));
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));
    }

    [Fact]
    public async Task TC03_a_survey_in_progress_keeps_blocking_its_whole_reserved_interval()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        await StartAsync(ctx, a, At(8));   // started early, not completed
        ctx.Clock.Now = At(9).ToUniversalTime();
        var refused = await BookAsync(ctx, "Customer B", At(10));
        Assert.False(refused.Succeeded);
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(10), At(11, 30)));
        Assert.Equal(AppointmentStatuses.Accepted, (await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId)).Status);
    }

    [Fact]
    public async Task TC04_a_cancelled_survey_is_not_busy()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(10), At(11, 30)));
        var row = await ctx.Db.TechnicianAppointments.SingleAsync(x => x.Id == a.AppointmentId);
        row.Status = AppointmentStatuses.Cancelled;   // synthetic cancelled row: the query semantics are under test
        row.CancelledAt = ctx.Clock.GetUtcNow();
        row.CancellationReason = "test";
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(10), At(11, 30)));
    }

    [Fact]
    public async Task TC06_TC07_the_configured_buffer_follows_the_real_end_and_cannot_be_bypassed()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        var buffer = await BufferMinutesAsync(ctx);
        Assert.True(buffer > 0, "the default policy has a buffer");
        await CompleteAsync(ctx, a, At(8), At(9, 15));
        // Ends 09:15 → the next start is allowed from 09:15 + buffer; anything closer is a conflict (also through the booking API).
        var earliest = At(9, 15).AddMinutes(buffer);
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, earliest.AddMinutes(-15), earliest.AddMinutes(75)));
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, earliest, earliest.AddMinutes(90)));
        var tooClose = await BookAsync(ctx, "Customer B", At(9, 30));   // 15 min after the real end
        Assert.False(tooClose.Succeeded);
        var allowed = await BookAsync(ctx, "Customer C", At(10));
        Assert.True(allowed.Succeeded, $"{allowed.ErrorCode} {allowed.Message}");
    }

    [Fact]
    public async Task A_survey_completed_before_its_reserved_start_collapses_to_its_completion_time_plus_the_buffer()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        await CompleteAsync(ctx, a, At(8), At(8, 45));
        var buffer = await BufferMinutesAsync(ctx);
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(ctx, a.Technician.Id, At(8, 45).AddMinutes(buffer - 5), At(11)));
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(8, 45).AddMinutes(buffer), At(10, 45)));
        // EffectiveBusy is the single rule: unit level.
        Assert.Equal((At(8, 45), At(8, 45)), EffectiveBusy.Of(At(10), At(11, 30), At(8, 45)));
        Assert.Equal((At(10), At(10, 20)), EffectiveBusy.Of(At(10), At(11, 30), At(10, 20)));
        // Finished late (overtime): busy until the confirmed completion, so the buffer follows the real end (2026-11-25 hardening).
        Assert.Equal((At(10), At(12)), EffectiveBusy.Of(At(10), At(11, 30), At(12)));
        Assert.Equal((At(10), At(11, 30)), EffectiveBusy.Of(At(10), At(11, 30), null));
    }

    [Fact]
    public async Task TC08_a_completed_survey_still_counts_toward_the_daily_survey_limit_as_documented()
    {
        var (ctx, a) = await SurveyAsync();
        await using var _ = ctx;
        // Admin configuration: at most one survey per day for this technician (SchedulingOptions: "survey appointments per technician per day").
        var profile = await ctx.Db.TechnicianProfiles.SingleAsync(p => p.UserId == a.Technician.Id);
        profile.MaxAppointmentsPerDay = 1;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        await CompleteAsync(ctx, a, At(8), At(9));
        // The time is free, but the day's survey count is reached: DAILY_LIMIT_REACHED, not a schedule conflict (the limit is not silently changed).
        Assert.Equal(EligibilityReasons.DailyLimitReached, await ReasonAsync(ctx, a.Technician.Id, At(13), At(14, 30)));
        Assert.False((await BookAsync(ctx, "Customer B", At(13))).Succeeded);
        // The next day is unaffected.
        Assert.Null(await ReasonAsync(ctx, a.Technician.Id, At(8, 0, Wed), At(9, 30, Wed)));
    }

    // ================================================================== installation (BR-03, BR-04, BR-05)

    internal sealed record Installation(InstallationWorkflowTestContext Ctx, PaymentPlanFlow.Agreed Flow, Guid ProjectId)
    {
        public Guid TechnicianId => Flow.TechnicianId;
    }

    /// <summary>30/70 contract, deposit, plan Wed 08:00 – Thu 12:00 confirmed by the paper signature, prerequisites, work started Wed 09:00.</summary>
    internal static async Task<Installation> InProgressAsync(InstallationWorkflowTestContext? on = null)
    {
        var ctx = on ?? InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        await PaymentPlanFlow.PayCashAsync(ctx, flow, 1);
        Assert.True((await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None)).Succeeded);
        await PaymentPlanFlow.SignOnPaperAsync(ctx, flow);
        var projectId = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First().ProjectId!.Value;
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, flow.Manager.Id);
        ctx.Clock.Now = At(9, 0, Wed).ToUniversalTime();
        Assert.True((await ctx.Installations().StartProjectAsync(flow.TechnicianId, projectId, None)).Succeeded);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, flow.TechnicianId)).Succeeded);
        return new Installation(ctx, flow, projectId);
    }

    private static async Task ReportAsync(Installation i, DateTimeOffset at)
    {
        i.Ctx.Clock.Now = at.ToUniversalTime();
        var reported = await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Array and inverter commissioned.", null, None);
        Assert.True(reported.Succeeded, reported.Message);
    }

    private static Task<InstallationAssignment> AssignmentAsync(Installation i)
        => i.Ctx.Db.InstallationAssignments.AsNoTracking().OrderByDescending(a => a.AssignedAt).FirstAsync(a => a.ProjectId == i.ProjectId);

    [Fact]
    public async Task TC14_an_installation_in_progress_keeps_blocking_its_interval()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(i.Ctx, i.TechnicianId, At(13, 0, Wed), At(14, 30, Wed)));
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(i.Ctx, i.TechnicianId, At(8, 0, Thu), At(9, 30, Thu)));
        Assert.Null((await AssignmentAsync(i)).WorkReportedAt);
    }

    [Fact]
    public async Task TC09_TC10_a_reported_completion_waits_for_the_customer_and_releases_the_rest_of_the_technician_interval()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        var reserved = await AssignmentAsync(i);
        await ReportAsync(i, At(11, 0, Wed));

        // TC09: the business workflow is unchanged — the customer still decides; the balance is not opened by the report.
        var project = await i.Ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId);
        Assert.Equal((InstallationStatuses.PendingCustomerAcceptance, (DateTimeOffset?)null), (project.Status, project.HandoverAt));
        var assignment = await AssignmentAsync(i);
        Assert.Equal((AssignmentStatuses.Accepted, reserved.StartsAt, reserved.EndsAt), (assignment.Status, assignment.StartsAt, assignment.EndsAt));
        Assert.Equal(At(11, 0, Wed).ToUniversalTime(), assignment.WorkReportedAt);
        Assert.Equal(InstallmentStatuses.Pending, (await PaymentPlanFlow.InstallmentAsync(i.Ctx, i.Flow.ContractId, 2)).Status);
        Assert.Contains(await i.Ctx.Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == i.ProjectId).Select(e => e.Detail).ToListAsync(),
            d => d != null && d.Contains("technician time released"));
        var staffAssignment = Assert.Single((await i.Ctx.Installations().GetForManagerAsync(i.ProjectId, None)).Value!.Assignments);
        Assert.Equal((AssignmentStatuses.Accepted, false), (staffAssignment.Status, staffAssignment.HoldsSchedule));
        Assert.NotNull(staffAssignment.WorkReportedAt);
        var technicianView = (await i.Ctx.Installations().GetForTechnicianAsync(i.TechnicianId, i.ProjectId, None)).Value!;
        Assert.Equal((InstallationStatuses.PendingCustomerAcceptance, false), (technicianView.Status, technicianView.ReworkScheduleRequired));
        Assert.NotNull(technicianView.WorkReportedAt);

        // TC10: the rest of Wed and Thursday morning are free again (buffer after the real end still applies).
        var buffer = await BufferMinutesAsync(i.Ctx);
        Assert.Equal(EligibilityReasons.ScheduleConflict, await ReasonAsync(i.Ctx, i.TechnicianId, At(11, 0, Wed), At(11, 0, Wed).AddMinutes(Math.Max(buffer, 1))));
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(13, 0, Wed), At(14, 30, Wed)));
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(8, 0, Thu), At(9, 30, Thu)));
        // Leave and working-schedule checks no longer see the released assignment as upcoming work.
        Assert.DoesNotContain(await i.Ctx.Scheduling.UnitOfWork.ListUpcomingInstallationAssignmentsAsync(i.TechnicianId, i.Ctx.Clock.GetUtcNow(), None),
            a => a.AppointmentId == assignment.Id);
        // A CONFIRMED plan is represented by its assignment: it does not block another plan of the technician on its own (BR-08).
        Assert.Empty(await new InstallationUnitOfWork(i.Ctx.Db).ListOverlappingLiveProposalsAsync(i.TechnicianId, Guid.NewGuid(), At(8, 0, Thu), At(12, 0, Thu), None));
    }

    [Fact]
    public async Task TC11_TC15_customer_accept_hands_over_opens_the_balance_and_the_completed_assignment_never_blocks_again()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        await ReportAsync(i, At(11, 0, Wed));
        i.Ctx.Clock.Now = At(16, 0, Wed).ToUniversalTime();
        await PaymentPlanFlow.AcceptAsync(i.Ctx, i.Flow, i.ProjectId);
        var project = await i.Ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId);
        Assert.Equal(InstallationStatuses.Completed, project.Status);
        Assert.Equal(At(16, 0, Wed).ToUniversalTime(), project.HandoverAt);
        Assert.Equal(AssignmentStatuses.Completed, (await AssignmentAsync(i)).Status);
        var balance = await PaymentPlanFlow.InstallmentAsync(i.Ctx, i.Flow.ContractId, 2);
        Assert.Equal(InstallmentStatuses.Due, balance.Status);   // the customer's ACCEPT opens the 70 %, never the technician's report
        Assert.Null(await ReasonAsync(i.Ctx, i.TechnicianId, At(8, 0, Thu), At(9, 30, Thu)));
    }

    [Fact]
    public async Task TC12_TC13_TC18_rework_needs_a_new_schedule_and_assignment_before_work_resumes_and_history_is_kept()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        await ReportAsync(i, At(11, 0, Wed));
        var original = await AssignmentAsync(i);
        i.Ctx.Clock.Now = At(14, 0, Wed).ToUniversalTime();
        var photo = await i.Ctx.VerifiedFileAsync(i.Flow.CustomerId, FilePurposes.AcceptanceEvidence);
        Assert.True((await i.Ctx.Installations().DecideAcceptanceAsync(i.Flow.CustomerId, i.ProjectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, "One panel is cracked", [photo], null), None)).Succeeded);

        // TC12: not completed, no handover, balance still closed, the reported assignment kept as is.
        var project = await i.Ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId);
        Assert.Equal((InstallationStatuses.ReworkRequired, (DateTimeOffset?)null), (project.Status, project.HandoverAt));
        Assert.Equal(InstallmentStatuses.Pending, (await PaymentPlanFlow.InstallmentAsync(i.Ctx, i.Flow.ContractId, 2)).Status);
        Assert.Equal(AssignmentStatuses.Accepted, (await AssignmentAsync(i)).Status);

        Assert.True((await i.Ctx.Installations().GetForTechnicianAsync(i.TechnicianId, i.ProjectId, None)).Value!.ReworkScheduleRequired);
        // TC13: the released assignment cannot carry renewed on-site work (evidence, report).
        var evidence = await i.Ctx.VerifiedFileAsync(i.TechnicianId, FilePurposes.InstallationEvidence);
        Assert.Equal(InstallationErrorCodes.ReworkScheduleRequired, (await i.Ctx.Installations().AddEvidenceAsync(i.TechnicianId, i.ProjectId, evidence, "rework", None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ReworkScheduleRequired,
            (await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Fixed", null, None)).ErrorCode);

        // A Manager schedules the rework (Fri 08:00–12:00, reason required); the technician accepts; work resumes on the new assignment.
        var manager = i.Flow.Manager.Id;
        var revision = (await i.Ctx.Installations().GetForManagerAsync(i.ProjectId, None)).Value!.Revision;
        Assert.Equal(InstallationErrorCodes.ReasonRequired, (await i.Ctx.Installations().AssignAsync(manager, i.ProjectId,
            new AssignTechnicianInput(i.TechnicianId, At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)), null, revision), None)).ErrorCode);
        var rescheduled = await i.Ctx.Installations().AssignAsync(manager, i.ProjectId,
            new AssignTechnicianInput(i.TechnicianId, At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)), "Rework: replace the cracked panel", revision), None);
        Assert.True(rescheduled.Succeeded, rescheduled.Message);
        Assert.Equal(InstallationErrorCodes.AssignmentNotAccepted,
            (await i.Ctx.Installations().AddEvidenceAsync(i.TechnicianId, i.ProjectId, evidence, "rework", None)).ErrorCode);
        Assert.True((await i.Ctx.Installations().AcceptAssignmentAsync(i.TechnicianId, i.ProjectId, null, None)).Succeeded);
        Assert.False((await i.Ctx.Installations().GetForTechnicianAsync(i.TechnicianId, i.ProjectId, None)).Value!.ReworkScheduleRequired);
        i.Ctx.Clock.Now = At(8, 30, Day.AddDays(3)).ToUniversalTime();
        Assert.True((await i.Ctx.Installations().AddEvidenceAsync(i.TechnicianId, i.ProjectId, evidence, "rework", None)).Succeeded);
        i.Ctx.Clock.Now = At(10, 0, Day.AddDays(3)).ToUniversalTime();
        Assert.True((await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "Panel replaced", null, None)).Succeeded);

        // TC18: history kept — the original assignment is REPLACED with its schedule and report time; the new one carries round 2.
        var assignments = await i.Ctx.Db.InstallationAssignments.AsNoTracking().Where(a => a.ProjectId == i.ProjectId).ToListAsync();
        var old = assignments.Single(a => a.Id == original.Id);
        Assert.Equal((AssignmentStatuses.Replaced, original.StartsAt, original.EndsAt, original.WorkReportedAt), (old.Status, old.StartsAt, old.EndsAt, old.WorkReportedAt));
        Assert.Equal(At(10, 0, Day.AddDays(3)).ToUniversalTime(), assignments.Single(a => a.Status == AssignmentStatuses.Accepted).WorkReportedAt);
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, (await i.Ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == i.ProjectId)).Status);
    }

    [Fact]
    public async Task TC19_released_time_still_obeys_working_hours_leave_and_zones()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        await ReportAsync(i, At(10, 0, Wed));
        Assert.Equal(EligibilityReasons.OutsideWorkingHours, await ReasonAsync(i.Ctx, i.TechnicianId, At(12, 0, Wed), At(13, 30, Wed)));   // lunch break
        Assert.Equal(EligibilityReasons.OutsideZone, await ReasonAsync(i.Ctx, i.TechnicianId, At(13, 0, Wed), At(14, 30, Wed), [Guid.NewGuid()]));
        i.Ctx.Db.TechnicianUnavailablePeriods.Add(new Modules.Scheduling.Entities.TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(), TechnicianUserId = i.TechnicianId, StartsAt = At(8, 0, Thu).ToUniversalTime(), EndsAt = At(12, 0, Thu).ToUniversalTime(),
            Reason = "Leave", CreatedAt = i.Ctx.Clock.GetUtcNow(), CreatedByUserId = i.Flow.Manager.Id,
        });
        await i.Ctx.Db.SaveChangesAsync();
        i.Ctx.Db.ChangeTracker.Clear();
        Assert.Equal(EligibilityReasons.OnLeave, await ReasonAsync(i.Ctx, i.TechnicianId, At(8, 0, Thu), At(9, 30, Thu)));
    }

    [Fact]
    public async Task Invalid_transitions_are_still_refused_after_a_reported_completion()
    {
        var i = await InProgressAsync();
        await using var _ = i.Ctx;
        await ReportAsync(i, At(11, 0, Wed));
        // A second report while the customer decides, a re-start, and a Manager reassignment while awaiting acceptance stay refused.
        Assert.Equal(InstallationErrorCodes.InvalidState, (await i.Ctx.Installations().ReportCompletionAsync(i.TechnicianId, i.ProjectId, "again", null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await i.Ctx.Installations().StartProjectAsync(i.TechnicianId, i.ProjectId, None)).ErrorCode);
        var revision = (await i.Ctx.Installations().GetForManagerAsync(i.ProjectId, None)).Value!.Revision;
        Assert.Equal(InstallationErrorCodes.InvalidState, (await i.Ctx.Installations().AssignAsync(i.Flow.Manager.Id, i.ProjectId,
            new AssignTechnicianInput(i.TechnicianId, At(8, 0, Thu), At(12, 0, Thu), "move", revision), None)).ErrorCode);
        // Another technician cannot act on the project.
        var stranger = await i.Ctx.Scheduling.SeedTechnicianAsync("Stranger", BinhTan);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await i.Ctx.Installations().ReportCompletionAsync(stranger.Id, i.ProjectId, "x", null, None)).ErrorCode);
    }
}
