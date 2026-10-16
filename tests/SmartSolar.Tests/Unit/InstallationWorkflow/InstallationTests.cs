using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

public sealed class InstallationTests
{
    private static readonly DateOnly Wed = Day.AddDays(1);
    private static readonly DateOnly Thu = Day.AddDays(2);

    public sealed record Paid(InstallationWorkflowTestContext Ctx, Guid ContractId, Guid CustomerId, Guid ManagerId);

    public static async Task<Paid> PaidContractAsync(InstallationWorkflowTestContext ctx)
    {
        var (ready, contract) = await ContractTests.SignedAsync(ctx);
        var customer = ready.Survey.Request.Customer.Id;
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, $"FT-{attempt.OrderCode}"), CancellationToken.None);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        return new Paid(ctx, contract.ContractId, customer, manager.Id);
    }

    private static IReadOnlyList<ShiftInput> Shifts(params (DateOnly Date, int Start, int End)[] shifts)
        => shifts.Select(s => new ShiftInput(s.Date, new TimeOnly(s.Start, 0), new TimeOnly(s.End, 0))).ToList();

    /// <summary>Project with two technicians (leader first) booked Wed 08–12, 13–17 and Thu 13–17.</summary>
    public static async Task<(Paid Paid, InstallationProjectView Project, Guid Leader, Guid Member)> ScheduledAsync(InstallationWorkflowTestContext ctx)
    {
        var paid = await PaidContractAsync(ctx);
        var leader = await ctx.Scheduling.SeedTechnicianAsync("Leader", District1);
        var member = await ctx.Scheduling.SeedTechnicianAsync("Member", District7);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, CancellationToken.None)).Value!;
        var scheduled = await ctx.Installations().ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [leader.Id, member.Id], leader.Id,
            Shifts((Wed, 8, 12), (Wed, 13, 17), (Thu, 13, 17)), CancellationToken.None);
        Assert.True(scheduled.Succeeded, scheduled.Message + " " + System.Text.Json.JsonSerializer.Serialize(scheduled.Details));
        return (paid, scheduled.Value!, leader.Id, member.Id);
    }

    internal static async Task<InstallationProjectView> CompleteAllMilestonesAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid leader, bool onlyNotCompleted = true)
    {
        var view = (await ctx.Installations().GetForTechnicianAsync(leader, projectId, CancellationToken.None)).Value!;
        foreach (var m in view.Milestones.Where(m => !onlyNotCompleted || m.Status != MilestoneStatuses.Completed))
        {
            if (m.Status == MilestoneStatuses.NotStarted) await ctx.Installations().StartMilestoneAsync(leader, projectId, m.MilestoneId, CancellationToken.None);
            var photo = await ctx.VerifiedFileAsync(leader, FilePurposes.InstallationEvidence);
            Assert.True((await ctx.Installations().AddEvidenceAsync(leader, projectId, m.MilestoneId, photo, m.Name, CancellationToken.None)).Succeeded);
            var done = await ctx.Installations().CompleteMilestoneAsync(leader, projectId, m.MilestoneId, CancellationToken.None);
            Assert.True(done.Succeeded, done.Message);
            view = done.Value!;
        }
        return view;
    }

    internal static Task<OperationResult<InstallationProjectView>> RegisterPanelAndInverterAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid leader, string suffix = "1")
        => ctx.Installations().RegisterAssetsAsync(leader, projectId,
        [
            new AssetInput("PANEL", null, "Jinko", "Tiger Neo 550", $"JK-PNL-{suffix}-001", "R1-C1", 144),
            new AssetInput("PANEL", null, "Jinko", "Tiger Neo 550", $"JK-PNL-{suffix}-002", "R1-C2", 144),
            new AssetInput("INVERTER", null, "Huawei", "SUN2000-5KTL", $"HW-INV-{suffix}", "Garage", 120),
        ], CancellationToken.None);

    [Fact]
    public async Task Projects_are_created_only_from_paid_contracts_once()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (ready, signed) = await ContractTests.SignedAsync(ctx);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");

        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, CancellationToken.None)).ErrorCode);
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(ready.Survey.Request.Customer.Id, signed.ContractId, CancellationToken.None)).Value!;
        await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "FT-P"), CancellationToken.None);
        Assert.Single(await ctx.Installations().ListReadyContractsAsync(CancellationToken.None));

        var project = await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, CancellationToken.None);
        Assert.Equal(InstallationStatuses.Planning, project.Value!.Status);
        Assert.Equal(7, project.Value.Milestones.Count);
        Assert.Equal(100, project.Value.Milestones.Sum(m => m.WeightPercent));
        Assert.All(project.Value.Checklist!, i => Assert.False(i.IsRequired));
        Assert.Equal(InstallationErrorCodes.ProjectExists, (await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, CancellationToken.None)).ErrorCode);
        Assert.Empty(await ctx.Installations().ListReadyContractsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Team_is_booked_atomically_on_shared_daily_shifts_with_exactly_one_leader()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, project, leader, member) = await ScheduledAsync(ctx);

        Assert.Equal(InstallationStatuses.Scheduled, project.Status);
        Assert.Equal(3, project.Shifts.Count);
        Assert.Equal(2, project.Team.Count);
        Assert.Equal(leader, Assert.Single(project.Team, m => m.IsLeader).TechnicianUserId);
        Assert.Equal(6, await ctx.Db.InstallationShiftAssignments.CountAsync(a => a.Status == "ACTIVE"));
        Assert.Equal(Wed, project.PlannedStartDate);
        Assert.Equal(Thu, project.PlannedEndDate);
        // The affected technician-days are queued for survey route recalculation.
        Assert.Contains(await ctx.Db.TechnicianScheduleRecalculations.AsNoTracking().ToListAsync(), r => r.TechnicianUserId == member && r.LocalDate == Wed);
    }

    [Fact]
    public async Task A_survey_appointment_blocks_the_shift_and_nobody_is_booked()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var leader = await ctx.Scheduling.SeedTechnicianAsync("Leader", District1);
        var busy = await ctx.Scheduling.SeedTechnicianAsync("Busy", District1);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(District7, "Other customer");
        foreach (var other in await ctx.Db.TechnicianProfiles.Where(p => p.UserId != leader.Id && p.UserId != busy.Id).ToListAsync()) other.IsSchedulingActive = false;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        // Only "Busy" can take this survey (the leader is on leave that day).
        await ctx.Scheduling.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(paid.ManagerId, leader.Id, At(9, 0, Wed), At(12, 0, Wed), "Errand"), CancellationToken.None);
        Assert.True((await ctx.BookWithInstallations().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9, 0, Wed), null), CancellationToken.None)).Succeeded);
        await ctx.Scheduling.Admin().RemoveUnavailablePeriodAsync(leader.Id, (await ctx.Db.TechnicianUnavailablePeriods.SingleAsync()).Id, CancellationToken.None);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, CancellationToken.None)).Value!;

        var preview = (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [leader.Id, busy.Id], leader.Id, Shifts((Wed, 8, 12), (Thu, 8, 12)), CancellationToken.None)).Value!;
        var confirm = await ctx.Installations().ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [leader.Id, busy.Id], leader.Id,
            Shifts((Wed, 8, 12), (Thu, 8, 12)), CancellationToken.None);

        Assert.False(preview.IsFeasible);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(busy.Id, conflict.TechnicianUserId);
        Assert.Equal(FeasibilityReasons.Overlap, conflict.Reason);
        Assert.Equal(InstallationErrorCodes.ScheduleConflict, confirm.ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationShiftAssignments.CountAsync());
    }

    [Fact]
    public async Task Installation_shifts_block_survey_slots_with_travel_but_do_not_count_toward_the_survey_limit()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        ctx.Scheduling.Travel.Minutes = _ => 20;
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Solo", District1, maxPerDay: 1);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, CancellationToken.None)).Value!;
        Assert.True((await ctx.Installations().ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [tech.Id], tech.Id,
            Shifts((Wed, 8, 12)), CancellationToken.None)).Succeeded);
        // Only the new technician exists for surveys from here on.
        foreach (var other in await ctx.Db.TechnicianProfiles.Where(p => p.UserId != tech.Id).ToListAsync()) other.IsSchedulingActive = false;
        await ctx.Db.SaveChangesAsync();

        var request = await ctx.Scheduling.SeedSurveyRequestAsync(District7, "Survey customer");
        var u = ctx.SchedulingWithInstallations();
        var slots = (await new GetSurveySlotsHandler(u, new Modules.Scheduling.Services.TechnicianAvailabilityEvaluator(ctx.Scheduling.Options, ctx.Clock),
            ctx.Scheduling.Travel, ctx.Scheduling.Calendar, ctx.Scheduling.Options, ctx.Clock)
            .HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Wed), CancellationToken.None)).Value!;

        Assert.DoesNotContain(slots.Slots, s => s.StartsAt < At(13, 0, Wed));     // 12:00 + 20 min + 30 min buffer > 12:30
        Assert.Contains(slots.Slots, s => s.StartsAt == At(13, 0, Wed));
        Assert.True((await ctx.BookWithInstallations().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(15, 0, Wed), null), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Earliest_departure_working_schedule_and_eligibility_are_enforced_per_technician()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        ctx.Scheduling.Travel.Minutes = _ => 60;
        var early = await ctx.Scheduling.SeedTechnicianAsync("Early", District1);
        var weekdayOff = await ctx.Scheduling.SeedTechnicianAsync("Off on Wednesday", District1);
        await ctx.Scheduling.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(weekdayOff.Id, false,
            [new WorkingPeriodInput(DayOfWeek.Thursday, new TimeOnly(8, 0), new TimeOnly(17, 0))]), CancellationToken.None);
        var inactive = await ctx.Scheduling.SeedTechnicianAsync("Inactive", District1, active: false);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, CancellationToken.None)).Value!;

        // 08:00 start, 60 min travel + 30 min buffer → leave at 06:30, before 07:00.
        var preview = (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [early.Id, weekdayOff.Id], early.Id, Shifts((Wed, 8, 12)), CancellationToken.None)).Value!;
        Assert.Contains(preview.Conflicts, c => c.TechnicianUserId == early.Id && c.Reason == FeasibilityReasons.BeforeEarliestDeparture);
        Assert.Contains(preview.Conflicts, c => c.TechnicianUserId == weekdayOff.Id && c.Reason == FeasibilityReasons.OutsideWorkingHours);

        Assert.Equal(InstallationErrorCodes.InvalidTeam, (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [inactive.Id], inactive.Id, Shifts((Wed, 9, 12)), CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidTeam, (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [early.Id], weekdayOff.Id, Shifts((Wed, 9, 12)), CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidShifts, (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [early.Id], early.Id, Shifts((Wed, 12, 9)), CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidShifts, (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [early.Id], early.Id, Shifts((Wed, 9, 12), (Wed, 11, 14)), CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidShifts, (await ctx.Installations().PreviewScheduleAsync(project.ProjectId, [early.Id], early.Id, Shifts((Day.AddDays(-2), 9, 12)), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Rescheduling_replaces_future_shifts_and_frees_the_old_slots()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, leader, member) = await ScheduledAsync(ctx);

        var replanned = await ctx.Installations().ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [leader], leader,
            Shifts((Thu, 8, 12)), CancellationToken.None);

        Assert.True(replanned.Succeeded, replanned.Message);
        Assert.Single(replanned.Value!.Shifts);
        Assert.Single(replanned.Value.Team);
        Assert.Equal(1, await ctx.Db.InstallationShiftAssignments.CountAsync(a => a.Status == "ACTIVE"));
        Assert.Equal(6, await ctx.Db.InstallationShiftAssignments.CountAsync(a => a.Status == "RELEASED"));
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await ctx.Installations().ConfirmScheduleAsync(paid.ManagerId, project.ProjectId, project.Revision, [leader], leader,
            Shifts((Thu, 13, 17)), CancellationToken.None)).ErrorCode);
        Assert.NotEqual(leader, member);
    }

    [Fact]
    public async Task Only_the_leader_executes_and_required_checklist_items_gate_the_start()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, leader, member) = await ScheduledAsync(ctx);
        var item = project.Checklist!.Single(i => i.Code == "GRID_CONNECTION_APPROVAL");

        Assert.Equal(InstallationErrorCodes.NotLeader, (await ctx.Installations().StartProjectAsync(member, project.ProjectId, CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await ctx.Installations().StartProjectAsync(Guid.NewGuid(), project.ProjectId, CancellationToken.None)).ErrorCode);

        var required = (await ctx.Installations().UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, project.Revision,
            new ChecklistUpdate(true, "PENDING", null, "EVN approval needed above 3 kWp"), CancellationToken.None)).Value!;
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, (await ctx.Installations().StartProjectAsync(leader, project.ProjectId, CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await ctx.Installations().UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, required.Revision,
            new ChecklistUpdate(true, "SATISFIED", null, null), CancellationToken.None)).ErrorCode);

        var permit = await ctx.VerifiedFileAsync(paid.ManagerId, FilePurposes.ProjectDocument, "pdf");
        var satisfied = await ctx.Installations().UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, required.Revision,
            new ChecklistUpdate(true, "SATISFIED", permit, "Approval letter"), CancellationToken.None);
        Assert.True(satisfied.Succeeded, satisfied.Message);
        var started = await ctx.Installations().StartProjectAsync(leader, project.ProjectId, CancellationToken.None);
        Assert.Equal(InstallationStatuses.InProgress, started.Value!.Status);
    }

    [Fact]
    public async Task Milestones_need_verified_photo_evidence_and_double_completion_changes_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, project, leader, member) = await ScheduledAsync(ctx);
        await ctx.Installations().StartProjectAsync(leader, project.ProjectId, CancellationToken.None);
        var milestone = project.Milestones[0];

        var started = (await ctx.Installations().StartMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, CancellationToken.None)).Value!;
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await ctx.Installations().CompleteMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, CancellationToken.None)).ErrorCode);
        var revision = started.Milestones[0].Revision;
        Assert.True((await ctx.Installations().UpdateProgressAsync(leader, project.ProjectId, milestone.MilestoneId, revision, 50, "Half done", CancellationToken.None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await ctx.Installations().UpdateProgressAsync(leader, project.ProjectId, milestone.MilestoneId, revision, 60, null, CancellationToken.None)).ErrorCode);

        var memberPhoto = await ctx.VerifiedFileAsync(member, FilePurposes.InstallationEvidence);
        Assert.Equal(InstallationErrorCodes.NotLeader, (await ctx.Installations().AddEvidenceAsync(member, project.ProjectId, milestone.MilestoneId, memberPhoto, null, CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned, (await ctx.Installations().AddEvidenceAsync(leader, project.ProjectId, milestone.MilestoneId, memberPhoto, null, CancellationToken.None)).ErrorCode);
        var photo = await ctx.VerifiedFileAsync(leader, FilePurposes.InstallationEvidence, "png");
        await ctx.Installations().AddEvidenceAsync(leader, project.ProjectId, milestone.MilestoneId, photo, "Site cleared", CancellationToken.None);

        var done = (await ctx.Installations().CompleteMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, CancellationToken.None)).Value!;
        var again = (await ctx.Installations().CompleteMilestoneAsync(leader, project.ProjectId, milestone.MilestoneId, CancellationToken.None)).Value!;
        Assert.Equal(MilestoneStatuses.Completed, done.Milestones[0].Status);
        Assert.Equal(done.Revision, again.Revision);
        Assert.Single(again.Events!, e => e.EventType == "MILESTONE_COMPLETED");
        Assert.Equal(10, again.ProgressPercent);
    }

    [Fact]
    public async Task Milestone_plan_is_editable_before_start_mandatory_steps_are_protected_and_later_changes_need_a_reason()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, leader, _) = await ScheduledAsync(ctx);
        var ms = project.Milestones;
        MilestoneInput Keep(MilestoneView m, int weight) => new(m.MilestoneId, m.Name, m.Description, weight);

        var withoutMandatory = ms.Where(m => !m.IsMandatory || m.Code != "ELECTRICAL_WIRING").Select(m => Keep(m, m.WeightPercent)).ToList();
        Assert.Equal(InstallationErrorCodes.InvalidMilestones, (await ctx.Installations().ReplaceMilestonesAsync(paid.ManagerId, project.ProjectId, project.Revision, withoutMandatory, null, CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidMilestones, (await ctx.Installations().ReplaceMilestonesAsync(paid.ManagerId, project.ProjectId, project.Revision,
            ms.Select(m => Keep(m, 1)).ToList(), null, CancellationToken.None)).ErrorCode);

        // Drop "Preparation", add a custom cleaning step, reorder: weights still total 100.
        var edited = ms.Where(m => m.Code != "PREPARATION").Select(m => Keep(m, m.WeightPercent)).ToList();
        edited.Insert(0, new MilestoneInput(null, "Roof cleaning", "Remove debris", 10));
        var result = await ctx.Installations().ReplaceMilestonesAsync(paid.ManagerId, project.ProjectId, project.Revision, edited, null, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Roof cleaning", result.Value!.Milestones[0].Name);
        Assert.Equal(Enumerable.Range(1, 7), result.Value.Milestones.Select(m => m.SortOrder));

        await ctx.Installations().StartProjectAsync(leader, project.ProjectId, CancellationToken.None);
        var current = (await ctx.Installations().GetForManagerAsync(project.ProjectId, CancellationToken.None)).Value!;
        var same = current.Milestones.Select(m => Keep(m, m.WeightPercent)).ToList();
        Assert.Equal(InstallationErrorCodes.MilestonesLocked, (await ctx.Installations().ReplaceMilestonesAsync(paid.ManagerId, project.ProjectId, current.Revision, same, null, CancellationToken.None)).ErrorCode);
        var revised = await ctx.Installations().ReplaceMilestonesAsync(paid.ManagerId, project.ProjectId, current.Revision, same, "Customer asked for a cleaner cable route", CancellationToken.None);
        Assert.Contains(revised.Value!.Events!, e => e.EventType == "MILESTONES_REVISED");
    }

    [Fact]
    public async Task Acceptance_rework_and_completion_keep_an_immutable_history_and_produce_a_handover()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, leader, member) = await ScheduledAsync(ctx);
        await ctx.Installations().StartProjectAsync(leader, project.ProjectId, CancellationToken.None);
        var installs = ctx.Installations();

        Assert.Equal(InstallationErrorCodes.MilestonesIncomplete, (await installs.RequestAcceptanceAsync(leader, project.ProjectId, "Done", CancellationToken.None)).ErrorCode);
        await CompleteAllMilestonesAsync(ctx, project.ProjectId, leader);
        Assert.Equal(InstallationErrorCodes.AssetsRequired, (await installs.RequestAcceptanceAsync(leader, project.ProjectId, "Done", CancellationToken.None)).ErrorCode);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, leader)).Succeeded);
        Assert.Equal(InstallationErrorCodes.DuplicateSerial, (await RegisterPanelAndInverterAsync(ctx, project.ProjectId, leader)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.NotLeader, (await installs.RequestAcceptanceAsync(member, project.ProjectId, "Done", CancellationToken.None)).ErrorCode);

        var pending = (await installs.RequestAcceptanceAsync(leader, project.ProjectId, "All panels installed and commissioned.", CancellationToken.None)).Value!;
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, pending.Status);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.DecideAcceptanceAsync(Guid.NewGuid(), project.ProjectId, true, null, CancellationToken.None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, " ", CancellationToken.None)).ErrorCode);

        var rework = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "Cable tray loose on the east side", CancellationToken.None)).Value!;
        Assert.Equal(InstallationStatuses.ReworkRequired, rework.Status);
        var wiring = rework.Milestones.Single(m => m.Code == "ELECTRICAL_WIRING");
        var reopened = (await installs.ReopenMilestoneAsync(leader, project.ProjectId, wiring.MilestoneId, CancellationToken.None)).Value!;
        Assert.Equal(1, reopened.Milestones.Single(m => m.Code == "ELECTRICAL_WIRING").ReworkRound);
        // Old evidence does not count for the new round.
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.CompleteMilestoneAsync(leader, project.ProjectId, wiring.MilestoneId, CancellationToken.None)).ErrorCode);
        var fixedView = await CompleteAllMilestonesAsync(ctx, project.ProjectId, leader);
        Assert.Equal(2, fixedView.Milestones.Single(m => m.Code == "ELECTRICAL_WIRING").Evidence.Count);

        await installs.RequestAcceptanceAsync(leader, project.ProjectId, "Cable tray fixed.", CancellationToken.None);
        var completed = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, CancellationToken.None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, completed.Status);
        Assert.Equal(new[] { AcceptanceDecisions.ReworkRequested, AcceptanceDecisions.Accepted }, completed.Acceptances.Select(a => a.Decision));
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "late", CancellationToken.None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await RegisterPanelAndInverterAsync(ctx, project.ProjectId, leader, "2")).ErrorCode);

        var handover = (await installs.GetHandoverAsync(paid.CustomerId, false, project.ProjectId, CancellationToken.None)).Value!;
        Assert.Equal(3, handover.Assets.Count);
        var inverter = handover.Assets.Single(a => a.AssetType == "INVERTER");
        Assert.Equal(ctx.Scheduling.Calendar.LocalDateOf(handover.HandoverAt).AddMonths(120), inverter.WarrantyEndsOn);
    }
    [Fact]
    public async Task Technicians_booked_on_installation_shifts_cannot_be_disabled_or_deactivated()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, _, leader, _) = await ScheduledAsync(ctx);
        var u = ctx.SchedulingWithInstallations();
        var admin = new AdminTechnicianHandlers(u, ctx.Scheduling.Recalculation(u), ctx.Scheduling.Calendar, ctx.Clock);

        var disabled = await admin.UpsertProfileAsync(new UpsertTechnicianProfileCommand(leader, District1.Latitude, District1.Longitude, null, false, null), CancellationToken.None);
        Assert.False(disabled.Succeeded);
        Assert.Contains("INSTALLATION_SHIFT", System.Text.Json.JsonSerializer.Serialize(disabled.Details));

        var work = await new SmartSolar.Infrastructure.Persistence.AdminUserStore(ctx.Db, includeInstallationShifts: true).GetOperationalWorkAsync(leader, ctx.Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(3, work.TechnicianAppointments.Count(a => a.Status == "INSTALLATION_SHIFT"));
        // With Installation Flow disabled the new tables are never queried.
        Assert.Empty((await new SmartSolar.Infrastructure.Persistence.AdminUserStore(ctx.Db).GetOperationalWorkAsync(leader, ctx.Clock.GetUtcNow(), CancellationToken.None)).TechnicianAppointments);
    }
}
