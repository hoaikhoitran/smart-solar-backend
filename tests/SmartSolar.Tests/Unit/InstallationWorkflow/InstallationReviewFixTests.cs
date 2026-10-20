using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;
using static SmartSolar.Tests.Unit.InstallationWorkflow.InstallationTests;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Final-review fixes of the Installation Flow: Manager return-to-rework, start after the scheduled interval, started work pinning
/// the technician's configuration, site area codes frozen by a project, coverage re-read under the area locks, and the outgoing
/// technician absorbing survey holds on a reassignment.
/// </summary>
public sealed class InstallationReviewFixTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);
    private static readonly DateOnly Thu = Day.AddDays(2);

    private static async Task<(Paid Paid, InstallationStaffView Project, Guid Technician)> PendingCustomerAcceptanceAsync(InstallationWorkflowTestContext ctx)
    {
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var reported = await ctx.Installations().ReportCompletionAsync(tech, project.ProjectId, "Done", null, None);
        Assert.True(reported.Succeeded, reported.Message);
        return (paid, (await ctx.Installations().GetForManagerAsync(project.ProjectId, None)).Value!, tech);
    }

    private static async Task OnlySchedulableAsync(InstallationWorkflowTestContext ctx, params Guid[] technicians)
    {
        await ctx.Db.TechnicianProfiles.Where(p => !technicians.Contains(p.UserId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSchedulingActive, false));
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Installation handlers whose first site-coverage read (the pre-read outside the transaction) returns <paramref name="stale"/>:
    /// the site's zone changed (committed) between that read and the FOR SHARE zone lock. Records every zone lock.
    /// </summary>
    private static (InstallationHandlers Handlers, List<IReadOnlyCollection<Guid>> AreaLocks) WithStalePreRead(InstallationWorkflowTestContext ctx, SiteCoverage stale)
    {
        var scheduling = ctx.SchedulingWithInstallations();
        var locks = new List<IReadOnlyCollection<Guid>>();
        var coverageReads = 0;
        var proxy = InterceptingProxy<ISchedulingUnitOfWork>.Create(scheduling,
            (method, args) =>
            {
                if (method.Name == nameof(ISchedulingUnitOfWork.LockZonesAsync)) locks.Add(((IEnumerable<Guid>)args[0]!).ToList());
            },
            (method, _) => method.Name == nameof(ISchedulingUnitOfWork.GetSiteCoverageAsync) && ++coverageReads == 1 ? Task.FromResult(stale) : null);
        var planner = new InstallationSchedulePlanner(ctx.Scheduling.Eligibility(scheduling), ctx.Scheduling.Calendar, ctx.InstallationOptions,
            ctx.Scheduling.Assignments(scheduling));
        return (new InstallationHandlers(new InstallationUnitOfWork(ctx.Db), proxy, planner, ctx.Files(), ctx.Scheduling.Calendar, ctx.InstallationOptions, ctx.Clock), locks);
    }

    /// <summary>
    /// Moves the site's commune to a new ACTIVE zone (the old mapping is soft-removed), optionally adding members to it; returns the
    /// coverage as it was before (the Bình Tân zone).
    /// </summary>
    private static async Task<(Guid NewZoneId, SiteCoverage Before)> MoveCommuneToNewZoneAsync(InstallationWorkflowTestContext ctx, Guid siteId, params Guid[] members)
    {
        var before = await ctx.SchedulingWithInstallations().GetSiteCoverageAsync(siteId, None);
        var commune = before.CommuneCode!;
        var admin = await ctx.Scheduling.ZoneAdminIdAsync();
        var mapping = await ctx.Db.OperationalZoneCommunes.SingleAsync(m => m.CommuneCode == commune && m.RemovedAt == null);
        mapping.RemovedAt = DateTimeOffset.UtcNow;
        mapping.RemovedByUserId = admin;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var zone = await ctx.Scheduling.EnsureZoneAsync("Z-NEW", [commune]);
        foreach (var member in members)
            ctx.Db.TechnicianZoneMemberships.Add(new TechnicianZoneMembership { Id = Guid.NewGuid(), TechnicianUserId = member, ZoneId = zone.Id, AssignedAt = DateTimeOffset.UtcNow });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        Assert.DoesNotContain(zone.Id, before.ZoneIds);
        return (zone.Id, before);
    }

    // ------------------------------------------------------------------ L1 return to rework

    [Fact]
    public async Task A_manager_returns_a_pending_completion_report_to_rework_and_new_evidence_is_required()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech) = await PendingCustomerAcceptanceAsync(ctx);
        var installs = ctx.Installations();

        Assert.Equal(InstallationErrorCodes.ReasonRequired, (await installs.ReturnToReworkAsync(paid.ManagerId, project.ProjectId, project.Revision, "  ", None)).ErrorCode);
        var tooLong = await installs.ReturnToReworkAsync(paid.ManagerId, project.ProjectId, project.Revision, new string('x', 501), None);
        Assert.Equal(CommonErrorCodes.ValidationFailed, tooLong.ErrorCode);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(tooLong.ErrorCode!));
        Assert.Equal(InstallationErrorCodes.StaleRevision, (await installs.ReturnToReworkAsync(paid.ManagerId, project.ProjectId, project.Revision - 1, "Photos missing", None)).ErrorCode);

        var returned = await installs.ReturnToReworkAsync(paid.ManagerId, project.ProjectId, project.Revision, "Inverter photo missing", None);

        Assert.True(returned.Succeeded, returned.Message);
        Assert.Equal(InstallationStatuses.ReworkRequired, returned.Value!.Status);
        Assert.Equal(2, returned.Value.CurrentRound);
        var round = Assert.Single(returned.Value.Acceptances);
        Assert.Equal(AcceptanceDecisions.ReworkRequested, round.Decision);
        Assert.Equal("Inverter photo missing", round.Reason);
        Assert.Equal(AcceptanceDeciders.Staff, round.DecidedBy);
        var stored = await ctx.Db.InstallationAcceptances.AsNoTracking().SingleAsync(a => a.ProjectId == project.ProjectId);
        Assert.Equal(paid.ManagerId, stored.DecidedByUserId);
        Assert.NotNull(stored.DecidedAt);
        var evt = Assert.Single(returned.Value.Events, e => e.EventType == InstallationEventTypes.ReturnedToRework);
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, evt.FromStatus);
        Assert.Equal(InstallationStatuses.ReworkRequired, evt.ToStatus);
        Assert.Equal(paid.ManagerId, evt.ActorUserId);

        // Not pending any more: a second return and a customer decision are refused; the customer sees who decided, not the manager's id.
        Assert.Equal(InstallationErrorCodes.InvalidState,
            (await installs.ReturnToReworkAsync(paid.ManagerId, project.ProjectId, returned.Value.Revision, "again", None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).ErrorCode);
        var customerView = (await installs.GetForCustomerAsync(paid.CustomerId, project.ProjectId, None)).Value!;
        Assert.Equal(AcceptanceDeciders.Staff, Assert.Single(customerView.Acceptances).DecidedBy);
        Assert.DoesNotContain(paid.ManagerId.ToString(), JsonSerializer.Serialize(customerView));

        // Round-1 evidence does not count; new evidence → report → only the customer completes.
        Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.ReportCompletionAsync(tech, project.ProjectId, "Fixed", null, None)).ErrorCode);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Inverter photo added", null, None)).Succeeded);
        var completed = (await installs.DecideAcceptanceAsync(paid.CustomerId, project.ProjectId, true, null, None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, completed.Status);
        Assert.Equal(new string?[] { AcceptanceDeciders.Staff, AcceptanceDeciders.Customer }, completed.Acceptances.Select(a => a.DecidedBy));
    }

    [Fact]
    public async Task Return_to_rework_is_refused_outside_pending_customer_acceptance()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await ScheduledAsync(ctx);
        await StartAsync(ctx, project.ProjectId, tech);
        var current = (await ctx.Installations().GetForManagerAsync(project.ProjectId, None)).Value!;

        var refused = await ctx.Installations().ReturnToReworkAsync(paid.ManagerId, project.ProjectId, current.Revision, "Not yet", None);

        Assert.Equal(InstallationErrorCodes.InvalidState, refused.ErrorCode);
        Assert.Equal(InstallationStatuses.InProgress, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void The_return_to_rework_endpoint_is_manager_only_and_rate_limited_like_other_manager_writes()
    {
        var action = typeof(ManagerInstallationsController).GetMethod(nameof(ManagerInstallationsController.ReturnToRework))!;
        var post = Assert.Single(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Cast<Microsoft.AspNetCore.Mvc.HttpPostAttribute>());
        Assert.Equal("installation-projects/{projectId:guid}/return-to-rework", post.Template);
        var limit = Assert.Single(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>());
        Assert.Equal(SmartSolar.Api.Extensions.RateLimitingExtensions.InstallationWritePolicy, limit.PolicyName);
        var roles = Assert.Single(typeof(ManagerInstallationsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()).Roles!;
        Assert.DoesNotContain("TECHNICIAN", roles);
        Assert.DoesNotContain("CUSTOMER", roles);
    }

    // ------------------------------------------------------------------ L2 expired interval, started work pins the technician

    [Fact]
    public async Task An_installation_cannot_start_after_its_scheduled_interval_ended()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, project, tech, _) = await ScheduledAsync(ctx);
        ctx.Clock.Now = At(17, 0, Wed).ToUniversalTime();                  // the Wed 08:00–17:00 interval just ended

        var started = await ctx.Installations().StartProjectAsync(tech, project.ProjectId, None);

        Assert.Equal(InstallationErrorCodes.ScheduleExpired, started.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(started.ErrorCode!));
        var view = (await ctx.Installations().GetForTechnicianAsync(tech, project.ProjectId, None)).Value!;
        Assert.False(view.CanStart);
        Assert.Equal(InstallationErrorCodes.ScheduleExpired, view.StartBlockedReason);
        Assert.Equal(InstallationStatuses.Scheduled, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Started_unfinished_work_blocks_disabling_area_removal_and_deactivation_after_its_interval_ended(bool reported)
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        Guid tech, manager;
        if (reported)
        {
            var (paid, _, t) = await PendingCustomerAcceptanceAsync(ctx);
            (tech, manager) = (t, paid.ManagerId);
        }
        else
        {
            var (paid, project, t, _) = await ScheduledAsync(ctx);
            await StartAsync(ctx, project.ProjectId, t);
            (tech, manager) = (t, paid.ManagerId);
        }
        ctx.Clock.Now = At(9, 0, Thu).ToUniversalTime();                   // the Wed interval is over, the work is not
        var admin = ctx.Scheduling.Admin();

        var disabled = await admin.UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech, false, null), None);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, disabled.ErrorCode);
        Assert.Contains(InstallationCommitments.Status, JsonSerializer.Serialize(disabled.Details));
        var district1 = await ctx.Scheduling.EnsureZoneAsync(District1);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments,
            (await admin.SetZonesAsync(new SetTechnicianZonesCommand(await ctx.Scheduling.ZoneAdminIdAsync(), tech, [district1.Id]), None)).ErrorCode);
        var work = await new AdminUserStore(ctx.Db, includeInstallationAssignments: true).GetOperationalWorkAsync(tech, ctx.Clock.GetUtcNow(), None);
        Assert.Single(work.TechnicianAppointments, a => a.Status == InstallationCommitments.Status);
        Assert.True((await ctx.Db.TechnicianProfiles.AsNoTracking().SingleAsync(p => p.UserId == tech)).IsSchedulingActive);
    }

    // ------------------------------------------------------------------ L5 site location frozen by a project

    [Fact]
    public async Task The_site_location_cannot_change_while_an_installation_project_is_unfinished()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var siteId = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).PropertySiteId;
        var change = new SetSiteLocationCommand(paid.CustomerId, siteId, "79", ThuDucWard);

        var refused = await ctx.Scheduling.Zones().SetSiteLocationAsync(change, None);

        Assert.Equal(SchedulingErrorCodes.SiteHasActiveInstallation, refused.ErrorCode);
        Assert.Equal(409, SchedulingControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Equal(BinhTanWard, (await ctx.Db.PropertySites.AsNoTracking().SingleAsync(s => s.Id == siteId)).CommuneCode);
        // Installation Flow disabled: the installation tables are not consulted.
        ctx.Scheduling.IncludeInstallationAssignments = false;
        Assert.NotEqual(SchedulingErrorCodes.SiteHasActiveInstallation, (await ctx.Scheduling.Zones().SetSiteLocationAsync(change, None)).ErrorCode);
        ctx.Scheduling.IncludeInstallationAssignments = true;
        await ctx.Db.PropertySites.Where(s => s.Id == siteId).ExecuteUpdateAsync(x => x.SetProperty(s => s.CommuneCode, BinhTanWard));

        Assert.True((await ctx.Installations().CancelAsync(paid.ManagerId, project.ProjectId, project.Revision, "Customer withdrew", None)).Succeeded);
        Assert.True((await ctx.Scheduling.Zones().SetSiteLocationAsync(change, None)).Succeeded);
    }

    // ------------------------------------------------------------------ L6 coverage re-read under the zone lock

    [Fact]
    public async Task Project_creation_locks_the_zone_it_finally_records()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var siteId = (await new InstallationUnitOfWork(ctx.Db).GetContractAsync(paid.ContractId, None))!.PropertySiteId;
        var (wardId, before) = await MoveCommuneToNewZoneAsync(ctx, siteId);
        var (handlers, locks) = WithStalePreRead(ctx, before);             // the commune moved to another zone after the pre-read

        var created = await handlers.CreateProjectAsync(paid.ManagerId, paid.ContractId, None);

        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(wardId, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).OperationalZoneId);
        Assert.Equal(2, locks.Count);
        Assert.DoesNotContain(wardId, locks[0]);
        Assert.Contains(wardId, locks[^1]);
    }

    [Fact]
    public async Task Assignment_records_the_zone_read_under_the_zone_lock()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var installer = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var siteId = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).PropertySiteId;
        var (wardId, before) = await MoveCommuneToNewZoneAsync(ctx, siteId, installer.Id);
        var (handlers, locks) = WithStalePreRead(ctx, before);

        var assigned = await handlers.AssignAsync(paid.ManagerId, project.ProjectId,
            new AssignTechnicianInput(installer.Id, At(8, 0, Wed), At(17, 0, Wed), null, project.Revision), None);

        Assert.True(assigned.Succeeded, assigned.Message + " " + JsonSerializer.Serialize(assigned.Details));
        Assert.Equal(wardId, assigned.Value!.Zone!.ZoneId);
        Assert.Equal(wardId, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).OperationalZoneId);
        Assert.Contains(wardId, locks[^1]);
        Assert.Single(await ctx.Db.InstallationAssignments.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------------ L7 the outgoing technician can absorb holds

    [Fact]
    public async Task Reassignment_hands_the_new_technicians_survey_hold_to_the_outgoing_technician()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await PaidContractAsync(ctx);
        var a = await ctx.Scheduling.SeedTechnicianAsync("Installer A", BinhTan);
        var b = await ctx.Scheduling.SeedTechnicianAsync("Installer B", BinhTan);
        await OnlySchedulableAsync(ctx, a.Id, b.Id);
        var installs = ctx.Installations();
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var toA = await installs.AssignAsync(paid.ManagerId, project.ProjectId, new AssignTechnicianInput(a.Id, At(8, 0, Wed), At(17, 0, Wed), null, project.Revision), None);
        Assert.True(toA.Succeeded, toA.Message);
        var open = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Open survey");
        var hold = await ctx.Scheduling.BookAsync(open, At(10, 0, Wed));
        Assert.Equal(b.Id, hold.ReservedTechnicianUserId);                 // A is busy installing: B holds the survey

        var toB = await installs.AssignAsync(paid.ManagerId, project.ProjectId,
            new AssignTechnicianInput(b.Id, At(8, 0, Wed), At(17, 0, Wed), "A is needed elsewhere", toA.Value!.Revision), None);

        Assert.True(toB.Succeeded, toB.ErrorCode + " " + toB.Message);
        Assert.Equal(b.Id, toB.Value!.ResponsibleTechnician!.TechnicianUserId);
        var moved = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(x => x.Id == hold.Id);
        Assert.Equal(a.Id, moved.ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, moved.Status);
        Assert.Equal(AssignmentStatuses.Replaced, (await ctx.Db.InstallationAssignments.AsNoTracking().SingleAsync(x => x.TechnicianUserId == a.Id)).Status);
    }
}
