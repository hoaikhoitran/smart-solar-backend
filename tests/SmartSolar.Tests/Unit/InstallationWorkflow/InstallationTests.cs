using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Installation Flow with one responsible technician: creation from PAID contracts, Manager assignment with the shared
/// eligibility rules (area, working days, leave, survey and installation conflicts with the buffer), technician
/// accept/decline/start/evidence/assets/completion report, customer acceptance or rework, cancellation, role-scoped views.
/// The paid contract's site lies in the synthetic Binh Tan district (code 79.777).
/// </summary>
public sealed class InstallationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);
    private static readonly DateOnly Thu = Day.AddDays(2);

    public sealed record Paid(InstallationWorkflowTestContext Ctx, Guid ContractId, Guid CustomerId, Guid ManagerId);

    public static async Task<Paid> PaidContractAsync(InstallationWorkflowTestContext ctx)
    {
        var (ready, contract) = await ContractTests.SignedAsync(ctx);
        var customer = ready.Survey.Request.Customer.Id;
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, $"FT-{attempt.OrderCode}"), None);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        return new Paid(ctx, contract.ContractId, customer, manager.Id);
    }

    private static AssignTechnicianInput Assign(Guid technician, DateTimeOffset start, DateTimeOffset end, int revision, string? reason = null)
        => new(technician, start, end, reason, revision);

    /// <summary>
    /// A PAID contract's project assigned to a Binh Tan technician for Wed 08:00–17:00 and accepted (SCHEDULED).
    /// The last value is a second, free Binh Tan technician.
    /// </summary>
    public static async Task<(Paid Paid, InstallationStaffView Project, Guid Technician, Guid Other)> ScheduledAsync(InstallationWorkflowTestContext ctx)
    {
        var paid = await PaidContractAsync(ctx);
        var technician = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var other = await ctx.Scheduling.SeedTechnicianAsync("Second installer", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var assigned = await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Assign(technician.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message + " " + JsonSerializer.Serialize(assigned.Details));
        var accepted = await ctx.Installations().AcceptAssignmentAsync(technician.Id, project.ProjectId, null, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        return (paid, (await ctx.Installations().GetForManagerAsync(project.ProjectId, None)).Value!, technician.Id, other.Id);
    }

    /// <summary>SCHEDULED → IN_PROGRESS on the scheduled day.</summary>
    internal static async Task StartAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid technician)
    {
        ctx.Clock.Now = At(8, 30, Wed).ToUniversalTime();
        var started = await ctx.Installations().StartProjectAsync(technician, projectId, None);
        Assert.True(started.Succeeded, started.Message);
    }

    /// <summary>One verified photo for the current round.</summary>
    internal static async Task<InstallationTechnicianView> AddCompletionEvidenceAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid technician)
    {
        var photo = await ctx.VerifiedFileAsync(technician, FilePurposes.InstallationEvidence);
        var added = await ctx.Installations().AddEvidenceAsync(technician, projectId, photo, "Array and inverter", None);
        Assert.True(added.Succeeded, added.Message);
        return added.Value!;
    }

    internal static Task<OperationResult<InstallationTechnicianView>> RegisterPanelAndInverterAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid technician, string suffix = "1")
        => ctx.Installations().RegisterAssetsAsync(technician, projectId,
        [
            new AssetInput("PANEL", null, "Jinko", "Tiger Neo 550", $"JK-PNL-{suffix}-001", "R1-C1", 144),
            new AssetInput("PANEL", null, "Jinko", "Tiger Neo 550", $"JK-PNL-{suffix}-002", "R1-C2", 144),
            new AssetInput("INVERTER", null, "Huawei", "SUN2000-5KTL", $"HW-INV-{suffix}", "Garage", 120),
        ], None);

    /// <summary>Only the given technician is schedulable from here on (other seeded technicians are switched off directly).</summary>
    private static async Task OnlySchedulableAsync(InstallationWorkflowTestContext ctx, params Guid[] technicians)
    {
        foreach (var other in await ctx.Db.TechnicianProfiles.Where(p => !technicians.Contains(p.UserId)).ToListAsync()) other.IsSchedulingActive = false;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
    }

    // ------------------------------------------------------------------ creation

    [Fact]
    public async Task Projects_are_created_only_from_paid_contracts_once_and_start_unassigned()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (ready, signed) = await ContractTests.SignedAsync(ctx);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");

        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ContractNotFound, (await ctx.Installations().CreateProjectAsync(manager.Id, Guid.NewGuid(), None)).ErrorCode);
        Assert.Empty(await ctx.Installations().ListReadyContractsAsync(None));
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(ready.Survey.Request.Customer.Id, signed.ContractId, None)).Value!;
        await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "FT-P"), None);
        Assert.Single(await ctx.Installations().ListReadyContractsAsync(None));

        var project = (await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None)).Value!;
        Assert.Equal(InstallationStatuses.PendingAssignment, project.Status);
        Assert.Null(project.ResponsibleTechnician);
        Assert.Equal("Binh Tan", project.Zone!.Name);
        Assert.Equal(3, project.Checklist.Count);
        Assert.All(project.Checklist, i => Assert.False(i.IsRequired));
        Assert.Equal(InstallationStatuses.PendingAssignment, Assert.Single(project.Events).ToStatus);
        Assert.Equal(InstallationErrorCodes.ProjectExists, (await ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.InstallationProjects.CountAsync(p => p.ContractId == signed.ContractId));
        Assert.Empty(await ctx.Installations().ListReadyContractsAsync(None));
    }

    // ------------------------------------------------------------------ assignment, acceptance, decline

    [Fact]
    public async Task Manager_assigns_the_responsible_technician_who_accepts_and_repeating_the_accept_changes_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;

        var assigned = (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None)).Value!;
        Assert.Equal(InstallationStatuses.PendingTechnicianAcceptance, assigned.Status);
        Assert.Equal(AssignmentStatuses.Pending, assigned.ResponsibleTechnician!.AssignmentStatus);
        Assert.Equal(At(8, 0, Wed), assigned.ScheduledStartsAt);
        Assert.Equal(Wed, assigned.PlannedStartDate);
        Assert.Null((await installs.GetForCustomerAsync(paid.CustomerId, project.ProjectId, None)).Value!.TechnicianName);   // not confirmed yet
        Assert.Equal(InstallationErrorCodes.StaleRevision,
            (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Thu), At(17, 0, Thu), project.Revision, "move"), None)).ErrorCode);

        var accepted = (await installs.AcceptAssignmentAsync(tech.Id, project.ProjectId, assigned.Revision, None)).Value!;
        Assert.Equal(InstallationStatuses.Scheduled, accepted.Status);
        Assert.Equal(AssignmentStatuses.Accepted, accepted.MyAssignmentStatus);
        var again = (await installs.AcceptAssignmentAsync(tech.Id, project.ProjectId, null, None)).Value!;
        Assert.Equal(accepted.Revision, again.Revision);
        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Single(staff.Events, e => e.EventType == InstallationEventTypes.AssignmentAccepted && e.ToStatus == InstallationStatuses.Scheduled);
        Assert.Equal("Installer", (await installs.GetForCustomerAsync(paid.CustomerId, project.ProjectId, None)).Value!.TechnicianName);
        Assert.Single(await installs.ListForTechnicianAsync(tech.Id, None));
    }

    [Fact]
    public async Task Eligibility_area_working_days_leave_and_interval_rules_are_enforced_for_every_day_of_the_interval()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var outside = await ctx.Scheduling.SeedTechnicianAsync("District 1 tech", District1);
        var thursdayOff = await ctx.Scheduling.SeedTechnicianAsync("Off on Thursday", BinhTan);
        Assert.True((await ctx.Scheduling.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(thursdayOff.Id, false,
            [new WorkingPeriodInput(DayOfWeek.Wednesday, new TimeOnly(8, 0), new TimeOnly(17, 0))]), None)).Succeeded);
        var onLeave = await ctx.Scheduling.SeedTechnicianAsync("Leave on Thursday", BinhTan);
        Assert.True((await ctx.Scheduling.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(paid.ManagerId, onLeave.Id, At(9, 0, Thu), At(11, 0, Thu), "Doctor"), None)).Succeeded);
        var disabled = await ctx.Scheduling.SeedTechnicianAsync("Disabled", BinhTan, active: false);
        var free = await ctx.Scheduling.SeedTechnicianAsync("Free", BinhTan);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var (start, end) = (At(8, 0, Wed), At(17, 0, Thu));   // two working days

        async Task<string?> Try(Guid technician, DateTimeOffset s, DateTimeOffset e)
            => (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(technician, s, e, project.Revision), None)).ErrorCode;

        // Another zone's technician needs an explicit cross-zone authorization with a reason (manual dispatch only).
        Assert.Equal(InstallationErrorCodes.CrossZoneAuthorizationRequired, await Try(outside.Id, start, end));
        Assert.Equal(409, OperationResultControllerBase.StatusFor(InstallationErrorCodes.CrossZoneAuthorizationRequired));
        Assert.Equal(409, OperationResultControllerBase.StatusFor(EligibilityReasons.OutsideZone));
        Assert.Equal(EligibilityReasons.OutsideWorkingHours, await Try(thursdayOff.Id, start, end));
        Assert.Equal(EligibilityReasons.OnLeave, await Try(onLeave.Id, start, end));
        Assert.Equal(EligibilityReasons.SchedulingDisabled, await Try(disabled.Id, start, end));
        Assert.Equal(EligibilityReasons.TechnicianInactive, await Try(paid.ManagerId, start, end));
        Assert.Equal(EligibilityReasons.OutsideWorkingHours, await Try(free.Id, At(7, 0, Wed), At(12, 0, Wed)));   // starts before working hours
        Assert.Equal(InstallationErrorCodes.InvalidSchedule, await Try(free.Id, At(12, 0, Wed), At(9, 0, Wed)));
        Assert.Equal(InstallationErrorCodes.InvalidSchedule, await Try(free.Id, At(8, 0, Day.AddDays(-2)), At(12, 0, Day.AddDays(-2))));
        Assert.Equal(InstallationErrorCodes.InvalidSchedule, await Try(free.Id, At(8, 0, Wed), At(12, 0, Wed.AddDays(20))));

        var candidates = (await installs.GetCandidatesAsync(project.ProjectId, start, end, None)).Value!;
        // Zone members first, then other zones flagged crossZone (eligible apart from the zone rule).
        Assert.True(candidates.Single(c => c.TechnicianUserId == outside.Id) is { CrossZone: true, IsEligible: true });
        Assert.True(candidates.TakeWhile(c => !c.CrossZone).Count() == candidates.Count(c => !c.CrossZone));
        Assert.True(candidates.Single(c => c.TechnicianUserId == free.Id) is { IsEligible: true, CrossZone: false });
        Assert.Equal(EligibilityReasons.OnLeave, candidates.Single(c => c.TechnicianUserId == onLeave.Id).Reason);

        // The multi-day interval spanning the lunch break and the night is fine for a free technician.
        var ok = await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(free.Id, start, end, project.Revision), None);
        Assert.True(ok.Succeeded, ok.Message);
        Assert.Equal(Thu, ok.Value!.PlannedEndDate);
    }

    [Fact]
    public async Task A_site_without_a_location_or_with_an_unserviceable_zone_cannot_be_assigned_and_without_a_zone_only_with_cross_zone_authorization()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        await ctx.Db.OperationalZones.ExecuteUpdateAsync(x => x.SetProperty(z => z.IsServiceable, false));
        Assert.Equal(InstallationErrorCodes.ZoneNotServiceable,
            (await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision), None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ZoneNotServiceable, (await ctx.Installations().GetCandidatesAsync(project.ProjectId, At(8, 0, Wed), At(12, 0, Wed), None)).ErrorCode);
        await ctx.Db.OperationalZones.ExecuteUpdateAsync(x => x.SetProperty(z => z.IsActive, false).SetProperty(z => z.IsServiceable, true));

        // No active zone maps the commune: every technician is cross-zone (explicit authorization + reason required).
        Assert.Equal(InstallationErrorCodes.CrossZoneAuthorizationRequired,
            (await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision), None)).ErrorCode);
        Assert.All((await ctx.Installations().GetCandidatesAsync(project.ProjectId, At(8, 0, Wed), At(12, 0, Wed), None)).Value!, c => Assert.True(c.CrossZone));
        await ctx.Db.PropertySites.ExecuteUpdateAsync(x => x.SetProperty(s => s.ProvinceCode, (string?)null).SetProperty(s => s.CommuneCode, (string?)null));
        Assert.Equal(InstallationErrorCodes.SiteLocationRequired,
            (await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision), None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.SiteLocationRequired, (await ctx.Installations().AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(tech.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision) with { CrossZoneAuthorized = true, Reason = "Nobody else" }, None)).ErrorCode);
        Assert.Equal(422, OperationResultControllerBase.StatusFor(InstallationErrorCodes.SiteLocationRequired));
    }

    [Fact]
    public async Task A_cross_zone_installer_needs_an_explicit_authorization_with_a_reason_and_keeps_every_other_rule()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var outside = await ctx.Scheduling.SeedTechnicianAsync("District 1 installer", District1);
        var onLeave = await ctx.Scheduling.SeedTechnicianAsync("District 7 on leave", District7);
        Assert.True((await ctx.Scheduling.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(paid.ManagerId, onLeave.Id, At(9, 0, Wed), At(10, 0, Wed), "Doctor"), None)).Succeeded);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var (start, end) = (At(8, 0, Wed), At(12, 0, Wed));

        // Without the flag, or with the flag but no reason: refused, nothing written.
        Assert.Equal(InstallationErrorCodes.CrossZoneAuthorizationRequired, (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(outside.Id, start, end, project.Revision), None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.CrossZoneAuthorizationRequired, (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(outside.Id, start, end, project.Revision) with { CrossZoneAuthorized = true }, None)).ErrorCode);
        // The authorization bypasses only the zone rule: leave still blocks.
        Assert.Equal(EligibilityReasons.OnLeave, (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(onLeave.Id, start, end, project.Revision, "Zone short of installers") with { CrossZoneAuthorized = true }, None)).ErrorCode);
        Assert.Empty(await ctx.Db.Set<SmartSolar.Modules.Scheduling.Dispatch.CrossZoneAuthorization>().ToListAsync());

        var ok = await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(outside.Id, start, end, project.Revision, "Zone short of installers") with { CrossZoneAuthorized = true }, None);
        Assert.True(ok.Succeeded, ok.Message);
        var authorization = Assert.Single(await ctx.Db.Set<SmartSolar.Modules.Scheduling.Dispatch.CrossZoneAuthorization>().ToListAsync());
        Assert.Equal((project.ProjectId, outside.Id, paid.ManagerId, "Zone short of installers"),
            (authorization.InstallationProjectId!.Value, authorization.TechnicianUserId, authorization.AuthorizedByUserId, authorization.Reason));
        Assert.Null(authorization.DispatchRequestId);
        Assert.Contains(ok.Value!.Events, e => e.Detail is { } d && d.Contains($"cross-zone authorization {authorization.Id}"));
    }

    [Fact]
    public async Task Declining_returns_the_project_to_pending_assignment_with_the_reason_visible_to_the_manager()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None);

        Assert.Equal(InstallationErrorCodes.ReasonRequired, (await installs.DeclineAssignmentAsync(tech.Id, project.ProjectId, " ", null, None)).ErrorCode);
        var declined = (await installs.DeclineAssignmentAsync(tech.Id, project.ProjectId, "Truck in repair that week", null, None)).Value!;
        Assert.Equal(AssignmentStatuses.Declined, declined.MyAssignmentStatus);
        Assert.Null(declined.Site);

        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Equal(InstallationStatuses.PendingAssignment, staff.Status);
        Assert.Null(staff.ResponsibleTechnician);
        Assert.Equal("Truck in repair that week", Assert.Single(staff.Assignments).ResponseReason);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.GetForTechnicianAsync(tech.Id, project.ProjectId, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.AcceptAssignmentAsync(tech.Id, project.ProjectId, null, None)).ErrorCode);
        // The interval is free again: the same technician can be assigned once more (the old row is history).
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(17, 0, Wed), staff.Revision), None)).Succeeded);
    }

    [Fact]
    public async Task Reassignment_keeps_history_and_every_reschedule_requires_a_reason_and_a_new_acceptance()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, first, second) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();

        Assert.Equal(InstallationErrorCodes.ReasonRequired,
            (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(second, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None)).ErrorCode);
        var reassigned = (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(second, At(8, 0, Wed), At(17, 0, Wed), project.Revision, "First installer is sick"), None)).Value!;
        Assert.Equal(InstallationStatuses.PendingTechnicianAcceptance, reassigned.Status);
        Assert.Equal(new[] { AssignmentStatuses.Replaced, AssignmentStatuses.Pending }, reassigned.Assignments.Select(a => a.Status));
        Assert.Contains(reassigned.Events, e => e.EventType == InstallationEventTypes.TechnicianReassigned && e.FromStatus == InstallationStatuses.Scheduled);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.GetForTechnicianAsync(first, project.ProjectId, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.StartProjectAsync(first, project.ProjectId, None)).ErrorCode);
        // The first technician's slot is free again for survey work.
        Assert.Empty(await ctx.SchedulingWithInstallations().ListUpcomingInstallationAssignmentsAsync(first, ctx.Clock.GetUtcNow(), None));

        Assert.True((await installs.AcceptAssignmentAsync(second, project.ProjectId, null, None)).Succeeded);
        var scheduled = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        var moved = (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(second, At(8, 0, Thu), At(17, 0, Thu), scheduled.Revision, "Rain forecast"), None)).Value!;
        Assert.Equal(InstallationStatuses.PendingTechnicianAcceptance, moved.Status);   // same technician, new time: accept again
        Assert.Equal(AssignmentStatuses.Pending, moved.ResponsibleTechnician!.AssignmentStatus);
        Assert.Contains(moved.Events, e => e.EventType == InstallationEventTypes.Rescheduled);
        Assert.Equal(3, moved.Assignments.Count);
    }

    // ------------------------------------------------------------------ conflicts with survey scheduling (both directions)

    [Fact]
    public async Task A_survey_appointment_blocks_an_overlapping_installation_assignment_including_the_buffer()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Busy", BinhTan);
        await OnlySchedulableAsync(ctx, tech.Id);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Survey customer");
        var booking = await ctx.Scheduling.BookAsync(request, At(13, 0, Wed));
        Assert.True((await ctx.Scheduling.AcceptAsync(tech.Id, booking.Id)).Succeeded);   // survey 13:00–14:30
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;

        Assert.Equal(EligibilityReasons.ScheduleConflict,
            (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None)).ErrorCode);
        Assert.Equal(EligibilityReasons.ScheduleConflict,   // ends 12:45: closer than the 30-minute buffer
            (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(12, 45, Wed), project.Revision), None)).ErrorCode);
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(12, 30, Wed), project.Revision), None)).Succeeded);
        Assert.Equal(1, await ctx.Db.InstallationAssignments.CountAsync());
    }

    [Fact]
    public async Task An_installation_assignment_blocks_survey_slots_with_the_buffer_and_survey_self_acceptance()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var installer = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan, maxPerDay: 1);
        var holder = await ctx.Scheduling.SeedTechnicianAsync("Holder", BinhTan);
        // A survey on Wed 10:00 is booked while only "Holder" is schedulable, so Holder keeps the capacity hold.
        await OnlySchedulableAsync(ctx, holder.Id);
        var open = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Open survey");
        var pending = await ctx.Scheduling.BookAsync(open, At(10, 0, Wed));
        Assert.Equal(holder.Id, pending.ReservedTechnicianUserId);
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == installer.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, true));

        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(installer.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision), None)).Succeeded);

        // Self-acceptance of the pending survey by the installer conflicts with the (still PENDING) installation assignment.
        Assert.Equal(EligibilityReasons.ScheduleConflict, (await ctx.Scheduling.AcceptAsync(installer.Id, pending.Id)).ErrorCode);

        // Survey slots: with only the installer schedulable, nothing before 12:00 + 30 min buffer (then the lunch break) is offered.
        await OnlySchedulableAsync(ctx, installer.Id);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Slot customer");
        var slots = (await ctx.Scheduling.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Wed), None)).Value!;
        Assert.DoesNotContain(slots.Slots, s => s.StartsAt < At(13, 0, Wed));
        Assert.Contains(slots.Slots, s => s.StartsAt == At(13, 0, Wed));
        Assert.False((await ctx.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(11, 0, Wed), null), None)).Succeeded);
        // Installation work does not count toward the survey daily limit (max 1).
        Assert.True((await ctx.Scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13, 0, Wed), null), None)).Succeeded);
    }

    [Fact]
    public async Task Another_installation_of_the_same_technician_blocks_an_overlapping_assignment()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, _, tech, _) = await ScheduledAsync(ctx);   // Wed 08:00–17:00
        ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var second = await PaidContractAsync(ctx);
        var project = (await ctx.Installations().CreateProjectAsync(second.ManagerId, second.ContractId, None)).Value!;

        Assert.Equal(EligibilityReasons.ScheduleConflict,
            (await ctx.Installations().AssignAsync(second.ManagerId, project.ProjectId, Assign(tech, At(16, 0, Wed), At(17, 0, Thu), project.Revision), None)).ErrorCode);
        Assert.True((await ctx.Installations().AssignAsync(second.ManagerId, project.ProjectId, Assign(tech, At(8, 0, Thu), At(17, 0, Thu), project.Revision), None)).Succeeded);
    }

    // ------------------------------------------------------------------ authorization and role-scoped views

    [Fact]
    public async Task Only_the_responsible_technician_can_read_or_act_and_a_pending_assignee_must_accept_first()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var tech = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", BinhTan);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(tech.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None);
        var strangerPhoto = await ctx.VerifiedFileAsync(stranger.Id, FilePurposes.InstallationEvidence);

        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.GetForTechnicianAsync(stranger.Id, project.ProjectId, None)).ErrorCode);
        Assert.Empty(await installs.ListForTechnicianAsync(stranger.Id, None));
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.AcceptAssignmentAsync(stranger.Id, project.ProjectId, null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.DeclineAssignmentAsync(stranger.Id, project.ProjectId, "no", null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.StartProjectAsync(stranger.Id, project.ProjectId, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.AddEvidenceAsync(stranger.Id, project.ProjectId, strangerPhoto, null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await RegisterPanelAndInverterAsync(ctx, project.ProjectId, stranger.Id)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.ReportCompletionAsync(stranger.Id, project.ProjectId, "done", null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.GetForCustomerAsync(stranger.Id, project.ProjectId, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.DecideAcceptanceAsync(Guid.NewGuid(), project.ProjectId, true, null, None)).ErrorCode);

        ctx.Clock.Now = At(8, 30, Wed).ToUniversalTime();
        Assert.Equal(InstallationErrorCodes.AssignmentNotAccepted, (await installs.StartProjectAsync(tech.Id, project.ProjectId, None)).ErrorCode);
        var view = (await installs.GetForTechnicianAsync(tech.Id, project.ProjectId, None)).Value!;
        Assert.False(view.CanStart);
        Assert.Equal(InstallationErrorCodes.AssignmentNotAccepted, view.StartBlockedReason);
        Assert.Equal("Customer", view.Site!.CustomerName);
    }

    [Fact]
    public async Task Customer_and_technician_responses_never_contain_staff_only_fields()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await StartAsync(ctx, project.ProjectId, tech);
        var evidence = await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var reported = (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).Value!;
        var decided = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "Loose cable", None)).Value!;

        static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var staffOnly = new[] { "\"checklist\"", "\"events\"", "\"assignments\"", "\"cancellationReason\"", "\"responsibleTechnician\"" };
        foreach (var body in new[] { Json(evidence), Json(reported), Json(decided), Json((await installs.GetForCustomerAsync(paid.CustomerId, project.ProjectId, None)).Value!),
                     Json(await installs.ListForCustomerAsync(paid.CustomerId, None)), Json(await installs.ListForTechnicianAsync(tech, None)) })
            Assert.All(staffOnly, field => Assert.DoesNotContain(field, body));
        // The customer never sees technician ids; the staff view has everything.
        Assert.DoesNotContain(tech.ToString(), Json(decided));
        var staff = Json((await installs.GetForManagerAsync(project.ProjectId, None)).Value!);
        Assert.All(staffOnly.Take(3), field => Assert.Contains(field, staff));
    }

    // ------------------------------------------------------------------ execution

    [Fact]
    public async Task Start_is_gated_by_the_scheduled_date_and_the_required_checklist_and_repeating_it_changes_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        var item = project.Checklist.Single(i => i.Code == "GRID_CONNECTION_APPROVAL");

        Assert.Equal(InstallationErrorCodes.TooEarlyToStart, (await installs.StartProjectAsync(tech, project.ProjectId, None)).ErrorCode);
        var required = (await installs.UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, project.Revision,
            new ChecklistUpdate(true, "PENDING", null, "EVN approval needed above 3 kWp"), None)).Value!;
        ctx.Clock.Now = At(8, 30, Wed).ToUniversalTime();
        var blocked = await installs.StartProjectAsync(tech, project.ProjectId, None);
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, blocked.ErrorCode);
        Assert.Contains("GRID_CONNECTION_APPROVAL", JsonSerializer.Serialize(blocked.Details));
        Assert.Equal(InstallationErrorCodes.ChecklistIncomplete, (await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!.StartBlockedReason);
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, required.Revision,
            new ChecklistUpdate(true, "SATISFIED", null, null), None)).ErrorCode);
        var permit = await ctx.VerifiedFileAsync(paid.ManagerId, FilePurposes.ProjectDocument, "pdf");
        Assert.True((await installs.UpdateChecklistItemAsync(paid.ManagerId, project.ProjectId, item.ItemId, required.Revision,
            new ChecklistUpdate(true, "SATISFIED", permit, "Approval letter"), None)).Succeeded);

        Assert.True((await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!.CanStart);
        var started = (await installs.StartProjectAsync(tech, project.ProjectId, None)).Value!;
        Assert.Equal(InstallationStatuses.InProgress, started.Status);
        var again = (await installs.StartProjectAsync(tech, project.ProjectId, None)).Value!;
        Assert.Equal(started.Revision, again.Revision);
        Assert.Single((await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Events, e => e.EventType == InstallationEventTypes.ProjectStarted);
    }

    [Fact]
    public async Task Evidence_must_be_a_verified_installation_photo_of_the_responsible_technician_used_once()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        var photo = await ctx.VerifiedFileAsync(tech, FilePurposes.InstallationEvidence);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.AddEvidenceAsync(tech, project.ProjectId, photo, null, None)).ErrorCode);   // not started
        await StartAsync(ctx, project.ProjectId, tech);

        Assert.Equal(FileErrorCodes.NotVerified,
            (await installs.AddEvidenceAsync(tech, project.ProjectId, await ctx.UnverifiedFileAsync(tech, FilePurposes.InstallationEvidence), null, None)).ErrorCode);
        Assert.Equal(FileErrorCodes.WrongPurpose,
            (await installs.AddEvidenceAsync(tech, project.ProjectId, await ctx.VerifiedFileAsync(tech, FilePurposes.ProjectDocument, "pdf"), null, None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned,
            (await installs.AddEvidenceAsync(tech, project.ProjectId, await ctx.VerifiedFileAsync(other, FilePurposes.InstallationEvidence), null, None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotFound, (await installs.AddEvidenceAsync(tech, project.ProjectId, Guid.NewGuid(), null, None)).ErrorCode);

        var added = (await installs.AddEvidenceAsync(tech, project.ProjectId, photo, "Mounting rails", None)).Value!;
        Assert.Equal(1, Assert.Single(added.Evidence).Round);
        Assert.Equal(FileErrorCodes.AlreadyLinked, (await installs.AddEvidenceAsync(tech, project.ProjectId, photo, null, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.InstallationEvidence.CountAsync());
        Assert.NotEqual(paid.ManagerId, tech);
    }

    [Fact]
    public async Task Completion_report_needs_evidence_of_the_current_round_and_panel_and_inverter_assets_and_cannot_be_repeated()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, project, tech, _) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).ErrorCode);
        await StartAsync(ctx, project.ProjectId, tech);

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.ReportCompletionAsync(tech, project.ProjectId, " ", null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.ReportCompletionAsync(tech, project.ProjectId, new string('x', 8001), null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).ErrorCode);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.Equal(InstallationErrorCodes.AssetsRequired, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).ErrorCode);
        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, [new AssetInput("PANEL", null, "Jinko", "Tiger Neo", "ONLY-PANEL", null, 144)], None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.AssetsRequired, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).ErrorCode);

        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        Assert.Equal(InstallationErrorCodes.DuplicateSerial, (await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.DuplicateSerial, (await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("METER", null, "EVN", "M1", "SAME", null, 12), new AssetInput("METER", null, "evn", "M1", " same ", null, 12)], None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("ROOF", null, "X", "Y", "Z", null, 12)], None)).ErrorCode);

        var reported = (await installs.ReportCompletionAsync(tech, project.ProjectId, "All panels installed and commissioned.", null, None)).Value!;
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, reported.Status);
        Assert.Equal(AcceptanceDecisions.Pending, Assert.Single(reported.Acceptances).Decision);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Again", null, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.InstallationAcceptances.CountAsync());
        Assert.Equal(InstallationErrorCodes.InvalidState, (await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech, "2")).ErrorCode);
    }

    [Fact]
    public async Task Customer_rework_opens_a_new_round_that_needs_new_evidence_then_acceptance_completes_and_hands_over()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None);

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, " ", None)).ErrorCode);
        var rework = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "Cable tray loose on the east side", None)).Value!;
        Assert.Equal(InstallationStatuses.ReworkRequired, rework.Status);
        Assert.Equal(2, rework.CurrentRound);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "again", None)).ErrorCode);

        // Round-1 evidence does not count for round 2.
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Fixed", null, None)).ErrorCode);
        var fixedView = await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.Equal(new[] { 1, 2 }, fixedView.Evidence.Select(e => e.Round));
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Cable tray fixed.", null, None)).Succeeded);

        var completed = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, completed.Status);
        Assert.Equal(new[] { AcceptanceDecisions.ReworkRequested, AcceptanceDecisions.Accepted }, completed.Acceptances.Select(a => a.Decision));
        var repeated = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Value!;   // idempotent
        Assert.Equal(completed.Revision, repeated.Revision);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "late", None)).ErrorCode);
        Assert.Single(await ctx.Db.InstallationEvents.Where(e => e.ProjectId == project.ProjectId && e.EventType == InstallationEventTypes.CustomerAccepted).ToListAsync());
        Assert.Equal(AssignmentStatuses.Completed, (await ctx.Db.InstallationAssignments.SingleAsync(a => a.TechnicianUserId == tech)).Status);
        // The technician still sees the completed job; the interval is no longer a busy commitment.
        Assert.Equal(AssignmentStatuses.Completed, (await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!.MyAssignmentStatus);
        Assert.Empty(await ctx.SchedulingWithInstallations().ListUpcomingInstallationAssignmentsAsync(tech, ctx.Clock.GetUtcNow(), None));

        var handover = (await installs.GetHandoverAsync(paid.CustomerId, false, project.ProjectId, None)).Value!;
        Assert.Equal(3, handover.Assets.Count);
        Assert.Equal(2, handover.Evidence.Count);
        Assert.Equal(2, handover.Acceptances.Count);
        Assert.Equal("Installer", handover.TechnicianName);
        var inverter = handover.Assets.Single(a => a.AssetType == "INVERTER");
        Assert.Equal(ctx.Scheduling.Calendar.LocalDateOf(handover.HandoverAt).AddMonths(120), inverter.WarrantyEndsOn);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.GetHandoverAsync(tech, false, project.ProjectId, None)).ErrorCode);
        Assert.True((await installs.GetHandoverAsync(paid.ManagerId, true, project.ProjectId, None)).Succeeded);
    }

    [Fact]
    public async Task Handover_is_unavailable_before_completion()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, _, _) = await ScheduledAsync(ctx);
        Assert.Equal(InstallationErrorCodes.NotHandedOver, (await ctx.Installations().GetHandoverAsync(paid.CustomerId, false, project.ProjectId, None)).ErrorCode);
    }

    [Fact]
    public async Task Reassignment_during_rework_keeps_the_status_and_the_new_technician_must_accept_before_working()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None);
        await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, false, "Fix the conduit", None);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.CancelAsync(paid.ManagerId, project.ProjectId,
            (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Revision, "too late", None)).ErrorCode);

        var current = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        var moved = (await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(other, At(8, 0, Thu), At(12, 0, Thu), current.Revision, "Rework by the second installer"), None)).Value!;
        Assert.Equal(InstallationStatuses.ReworkRequired, moved.Status);
        var photo = await ctx.VerifiedFileAsync(other, FilePurposes.InstallationEvidence);
        Assert.Equal(InstallationErrorCodes.AssignmentNotAccepted, (await installs.AddEvidenceAsync(other, project.ProjectId, photo, null, None)).ErrorCode);
        Assert.True((await installs.AcceptAssignmentAsync(other, project.ProjectId, null, None)).Succeeded);
        Assert.Equal(InstallationStatuses.ReworkRequired, (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Status);
        Assert.True((await installs.AddEvidenceAsync(other, project.ProjectId, photo, "Conduit fixed", None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Fixed", null, None)).ErrorCode);
        Assert.True((await installs.ReportCompletionAsync(other, project.ProjectId, "Fixed", null, None)).Succeeded);
    }

    // ------------------------------------------------------------------ concurrency (revision) and cancellation

    [Fact]
    public async Task A_stale_revision_loses_completion_vs_reassignment_in_either_order()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var seenByManager = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        var seenByTechnician = (await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!;

        // The technician reports first: the manager's reassignment based on the old revision is rejected.
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", seenByTechnician.Revision, None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(other, At(8, 0, Thu), At(12, 0, Thu), seenByManager.Revision, "Swap"), None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(other, At(8, 0, Thu), At(12, 0, Thu), (await installs.GetForManagerAsync(project.ProjectId, None)).Value!.Revision, "Swap"), None)).ErrorCode);
    }

    [Fact]
    public async Task A_reassignment_first_makes_the_old_technicians_completion_fail()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var seenByTechnician = (await installs.GetForTechnicianAsync(tech, project.ProjectId, None)).Value!;
        var current = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(other, At(8, 0, Thu), At(12, 0, Thu), current.Revision, "Swap"), None)).Succeeded);

        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", seenByTechnician.Revision, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.InstallationAcceptances.CountAsync());
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await installs.AcceptAssignmentAsync(other, project.ProjectId, seenByTechnician.Revision, None)).ErrorCode);
    }

    [Fact]
    public async Task Cancellation_needs_a_reason_is_allowed_only_before_work_starts_frees_the_technician_and_leaves_the_contract_paid()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();

        Assert.Equal(InstallationErrorCodes.ReasonRequired, (await installs.CancelAsync(paid.ManagerId, project.ProjectId, project.Revision, null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await installs.CancelAsync(paid.ManagerId, project.ProjectId, project.Revision - 1, "x", None)).ErrorCode);
        var cancelled = (await installs.CancelAsync(paid.ManagerId, project.ProjectId, project.Revision, "Customer moved house", None)).Value!;
        Assert.Equal(InstallationStatuses.Cancelled, cancelled.Status);
        Assert.Equal("Customer moved house", cancelled.CancellationReason);
        Assert.Equal(AssignmentStatuses.Cancelled, Assert.Single(cancelled.Assignments).Status);
        Assert.Equal(cancelled.Revision, (await installs.CancelAsync(paid.ManagerId, project.ProjectId, cancelled.Revision, "again", None)).Value!.Revision);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            Assign(tech, At(8, 0, Wed), At(17, 0, Wed), cancelled.Revision, "revive"), None)).ErrorCode);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.SingleAsync(c => c.Id == paid.ContractId)).Status);
        Assert.Empty(await installs.ListReadyContractsAsync(None));   // one project per contract, even when cancelled
        Assert.Equal(InstallationStatuses.Cancelled, Assert.Single(await installs.ListForCustomerAsync(paid.CustomerId, None)).Status);
        Assert.Empty(await ctx.SchedulingWithInstallations().ListUpcomingInstallationAssignmentsAsync(tech, ctx.Clock.GetUtcNow(), None));

        // An in-progress project cannot be cancelled.
        ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var (_, second, secondTech, _) = await ScheduledAsync(ctx);
        await StartAsync(ctx, second.ProjectId, secondTech);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.CancelAsync(paid.ManagerId, second.ProjectId,
            (await installs.GetForManagerAsync(second.ProjectId, None)).Value!.Revision, "No", None)).ErrorCode);
    }

    // ------------------------------------------------------------------ admin guards

    [Fact]
    public async Task Technicians_with_installation_assignments_cannot_be_disabled_put_on_leave_moved_out_of_the_area_or_deactivated()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, _, tech, _) = await ScheduledAsync(ctx);
        var admin = ctx.Scheduling.Admin();

        var disabled = await admin.UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech, false, null), None);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, disabled.ErrorCode);
        Assert.Contains(InstallationCommitments.Status, JsonSerializer.Serialize(disabled.Details));
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, (await admin.AddUnavailablePeriodAsync(
            new AddUnavailablePeriodCommand(paid.ManagerId, tech, At(13, 0, Wed), At(14, 0, Wed), "Errand"), None)).ErrorCode);
        var district1 = await ctx.Scheduling.EnsureZoneAsync(District1);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, (await admin.SetZonesAsync(
            new SetTechnicianZonesCommand(await ctx.Scheduling.ZoneAdminIdAsync(), tech, [district1.Id]), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, (await admin.SetWorkingScheduleAsync(new SetWorkingScheduleCommand(tech, false,
            [new WorkingPeriodInput(DayOfWeek.Thursday, new TimeOnly(8, 0), new TimeOnly(17, 0))]), None)).ErrorCode);
        // Leave on another day is fine.
        Assert.True((await admin.AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(paid.ManagerId, tech, At(13, 0, Thu), At(14, 0, Thu), "Errand"), None)).Succeeded);

        var work = await new SmartSolar.Infrastructure.Persistence.AdminUserStore(ctx.Db, includeInstallationAssignments: true).GetOperationalWorkAsync(tech, ctx.Clock.GetUtcNow(), None);
        Assert.Single(work.TechnicianAppointments, a => a.Status == InstallationCommitments.Status);
        // With Installation Flow disabled the installation tables are never queried.
        Assert.DoesNotContain((await new SmartSolar.Infrastructure.Persistence.AdminUserStore(ctx.Db).GetOperationalWorkAsync(tech, ctx.Clock.GetUtcNow(), None)).TechnicianAppointments,
            a => a.Status == InstallationCommitments.Status);
    }

    // ------------------------------------------------------------------ review rulings: survey holds are re-homed, areas in use

    [Fact]
    public async Task An_installers_own_pending_survey_hold_is_rehomed_instead_of_blocking_the_assignment()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var installer = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var spare = await ctx.Scheduling.SeedTechnicianAsync("Spare", BinhTan);
        await OnlySchedulableAsync(ctx, installer.Id);
        var open = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Open survey");
        var hold = await ctx.Scheduling.BookAsync(open, At(10, 0, Wed));
        Assert.Equal(installer.Id, hold.ReservedTechnicianUserId);
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == spare.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, true));
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;

        var candidates = (await installs.GetCandidatesAsync(project.ProjectId, At(8, 0, Wed), At(17, 0, Wed), None)).Value!;
        Assert.True(candidates.Single(c => c.TechnicianUserId == installer.Id).IsEligible);
        var assigned = await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(installer.Id, At(8, 0, Wed), At(17, 0, Wed), project.Revision), None);

        Assert.True(assigned.Succeeded, assigned.Message + " " + JsonSerializer.Serialize(assigned.Details));
        var moved = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == hold.Id);
        Assert.Equal(spare.Id, moved.ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, moved.Status);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().ToListAsync(),
            e => e.AppointmentId == hold.Id && e.EventType == SmartSolar.Modules.Scheduling.Entities.AppointmentEventTypes.HoldMoved && e.NewTechnicianUserId == spare.Id);
        Assert.Equal(1, await ctx.Db.InstallationAssignments.CountAsync(a => a.TechnicianUserId == installer.Id));
    }

    [Fact]
    public async Task An_installers_hold_that_nobody_can_take_over_refuses_the_assignment_and_changes_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var installer = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        await OnlySchedulableAsync(ctx, installer.Id);
        var open = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Open survey");
        var hold = await ctx.Scheduling.BookAsync(open, At(15, 30, Wed));       // 15:30 is within the 30-minute buffer of a 15:15 end
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;

        var refused = await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(installer.Id, At(8, 0, Wed), At(15, 15, Wed), project.Revision), None);

        Assert.Equal(EligibilityReasons.ScheduleConflict, refused.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        var unchanged = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == hold.Id);
        Assert.Equal(installer.Id, unchanged.ReservedTechnicianUserId);
        Assert.Equal(hold.Revision, unchanged.Revision);
        Assert.Equal(0, await ctx.Db.InstallationAssignments.CountAsync());
        Assert.Equal(InstallationStatuses.PendingAssignment, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
        // A hold far enough away does not matter.
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, Assign(installer.Id, At(8, 0, Wed), At(12, 0, Wed), project.Revision), None)).Succeeded);
        Assert.Equal(installer.Id, (await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == hold.Id)).ReservedTechnicianUserId);
    }

    [Fact]
    public async Task A_zone_with_an_unfinished_installation_project_cannot_be_deactivated()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var area = await ctx.Scheduling.EnsureZoneAsync("Z-BINH-TAN");

        var refused = await ctx.Scheduling.Zones().UpdateAsync(
            new UpdateZoneCommand(await ctx.Scheduling.ZoneAdminIdAsync(), area.Id, area.Name, false, null, null, area.Revision), None);
        Assert.Equal(SchedulingErrorCodes.ZoneInUse, refused.ErrorCode);
        Assert.Contains("\"blockingInstallationProjects\":1", JsonSerializer.Serialize(refused.Details));
        Assert.Contains("\"blockingAppointments\":0", JsonSerializer.Serialize(refused.Details));   // the completed survey does not count

        Assert.True((await installs.CancelAsync(paid.ManagerId, project.ProjectId, project.Revision, "Customer withdrew", None)).Succeeded);
        Assert.True((await ctx.Scheduling.Zones().UpdateAsync(
            new UpdateZoneCommand(await ctx.Scheduling.ZoneAdminIdAsync(), area.Id, area.Name, false, null, null, area.Revision), None)).Succeeded);
    }
}
