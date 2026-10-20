using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Once the confirmed technician started the on-site survey (report IN_PROGRESS) the appointment is frozen: no release, no Sales
/// or customer reschedule, no customer cancel — checked before and again under the technician lock that starting the survey takes.
/// A technician can also no longer release from the earliest survey start (OnSiteSurvey:EarlyStartMinutes) onwards.
/// </summary>
public sealed class OnSiteSurveyFreezeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Confirmed 10:00 survey whose technician started the on-site survey at 08:30.</summary>
    private static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey)> StartedAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        var s = await ctx.ConfirmedSurveyAsync();
        await ctx.Scheduling.SeedTechnicianAsync("Spare", BinhTan);
        ctx.Clock.Now = At(8, 30).ToUniversalTime();
        var started = await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, None);
        Assert.True(started.Succeeded, started.Message);
        return (ctx, s);
    }

    private static async Task AssertUnchangedAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s)
    {
        var stored = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == s.AppointmentId);
        Assert.Equal(AppointmentStatuses.Accepted, stored.Status);
        Assert.Equal(s.Technician.Id, stored.AssignedTechnicianUserId);
        Assert.Equal(At(10).ToUniversalTime(), stored.StartsAt.ToUniversalTime());
        Assert.Single(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
    }

    /// <summary>The report back to DRAFT, so only the in-transaction check can see the start (flipped when the technicians are locked).</summary>
    private static async Task<ISchedulingUnitOfWork> StartsInsideTheLockAsync(InstallationWorkflowTestContext ctx)
    {
        await ctx.Db.OnSiteSurveyReports.ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, OnSiteSurveyStatuses.Draft));
        var flipped = false;
        return InterceptingProxy<ISchedulingUnitOfWork>.Create(new SchedulingUnitOfWork(ctx.Db, includeInstallationAssignments: true), (method, _) =>
        {
            if (flipped || method.Name != nameof(ISchedulingUnitOfWork.LockTechniciansAsync)) return;
            flipped = true;
            ctx.Db.OnSiteSurveyReports.ExecuteUpdate(x => x.SetProperty(r => r.Status, OnSiteSurveyStatuses.InProgress));
        });
    }

    private static TechnicianJobHandlers Jobs(InstallationWorkflowTestContext ctx, ISchedulingUnitOfWork u)
    {
        var eligibility = new TechnicianEligibility(u, ctx.Scheduling.Calendar, ctx.Scheduling.Options, ctx.Scheduling.Policies);
        return new(u, new AppointmentAssignmentService(u, eligibility, ctx.Scheduling.Options, ctx.Clock, ctx.Scheduling.DispatchStore), eligibility, ctx.Scheduling.Calendar, ctx.Clock);
    }

    private static AppointmentMover Mover(InstallationWorkflowTestContext ctx, ISchedulingUnitOfWork u)
    {
        var o = ctx.Scheduling.Options;
        var eligibility = new TechnicianEligibility(u, ctx.Scheduling.Calendar, o, ctx.Scheduling.Policies);
        return new(u, new AppointmentReservationService(u, eligibility, o, ctx.Clock), new AppointmentAssignmentService(u, eligibility, o, ctx.Clock, ctx.Scheduling.DispatchStore), o, ctx.Clock);
    }

    [Fact]
    public async Task The_technician_cannot_release_once_the_on_site_survey_is_in_progress()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        ctx.Scheduling.OnSiteOptions.EarlyStartMinutes = 0;   // isolate the report check from the time cut-off

        var released = await ctx.Scheduling.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(s.Technician.Id, s.AppointmentId, "Sick"), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, released.ErrorCode);
        Assert.Equal(409, SmartSolar.Api.Controllers.SchedulingControllerBase.StatusFor(released.ErrorCode!));
        await AssertUnchangedAsync(ctx, s);
    }

    [Fact]
    public async Task Release_rechecks_the_on_site_survey_under_the_technician_lock()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        ctx.Scheduling.OnSiteOptions.EarlyStartMinutes = 0;
        var u = await StartsInsideTheLockAsync(ctx);

        var released = await Jobs(ctx, u).ReleaseAsync(new ReleaseAppointmentCommand(s.Technician.Id, s.AppointmentId, "Sick"), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, released.ErrorCode);
        ctx.Db.ChangeTracker.Clear();
        await AssertUnchangedAsync(ctx, s);
    }

    [Fact]
    public async Task Release_is_refused_from_the_earliest_on_site_survey_start_onwards()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.ConfirmedSurveyAsync();
        await ctx.Scheduling.SeedTechnicianAsync("Spare", BinhTan);

        ctx.Clock.Now = At(8).ToUniversalTime();                   // exactly EarlyStartMinutes (120) before the 10:00 start
        var tooLate = await ctx.Scheduling.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(s.Technician.Id, s.AppointmentId, "Sick"), None);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, tooLate.ErrorCode);
        await AssertUnchangedAsync(ctx, s);

        ctx.Clock.Now = At(7, 59).ToUniversalTime();
        var inTime = await ctx.Scheduling.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(s.Technician.Id, s.AppointmentId, "Sick"), None);
        Assert.True(inTime.Succeeded, inTime.Message);
    }

    [Fact]
    public async Task Sales_cannot_reschedule_or_reassign_through_a_reschedule_once_the_survey_started()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        var other = await ctx.Scheduling.SeedTechnicianAsync("Other", BinhTan);

        var moved = await ctx.Scheduling.SalesReschedule().HandleAsync(new SalesRescheduleCommand(s.Sales.Id, s.Request.Request.Id, At(14), "Customer asked", null), None);
        var swapped = await ctx.Scheduling.SalesReschedule().HandleAsync(new SalesRescheduleCommand(s.Sales.Id, s.Request.Request.Id, At(14), "Swap", other.Id), None);
        var sameTimeOtherTech = await ctx.Scheduling.SalesReschedule().HandleAsync(new SalesRescheduleCommand(s.Sales.Id, s.Request.Request.Id, At(10), "Swap", other.Id), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, moved.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, swapped.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, sameTimeOtherTech.ErrorCode);
        await AssertUnchangedAsync(ctx, s);
        // Same start without a technician stays the idempotent no-op.
        Assert.True((await ctx.Scheduling.SalesReschedule().HandleAsync(new SalesRescheduleCommand(s.Sales.Id, s.Request.Request.Id, At(10), null, null), None)).Succeeded);
    }

    [Fact]
    public async Task A_reschedule_rechecks_the_on_site_survey_under_the_technician_lock()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        var u = await StartsInsideTheLockAsync(ctx);
        var handler = new SalesRescheduleHandler(u, Mover(ctx, u), new TechnicianEligibility(u, ctx.Scheduling.Calendar, ctx.Scheduling.Options, ctx.Scheduling.Policies),
            ctx.Scheduling.Calendar, ctx.Clock);

        var moved = await handler.HandleAsync(new SalesRescheduleCommand(s.Sales.Id, s.Request.Request.Id, At(14), "Customer asked", null), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, moved.ErrorCode);
        ctx.Db.ChangeTracker.Clear();
        await AssertUnchangedAsync(ctx, s);
    }

    [Fact]
    public async Task The_customer_cannot_reschedule_or_cancel_once_the_survey_started_whatever_the_change_lead()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        ctx.Scheduling.Options.CustomerChangeLeadMinutes = 30;     // the lead alone would still allow changes at 08:30

        var rescheduled = await ctx.Scheduling.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(s.Request.Customer.Id, s.Request.Request.Id, At(14), null), None);
        var cancelled = await ctx.Scheduling.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(s.Request.Customer.Id, s.Request.Request.Id, "Changed my mind"), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, rescheduled.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, cancelled.ErrorCode);
        await AssertUnchangedAsync(ctx, s);
    }

    [Fact]
    public async Task A_customer_cancel_rechecks_the_on_site_survey_under_the_technician_lock()
    {
        var (ctx, s) = await StartedAsync();
        await using var _ = ctx;
        ctx.Scheduling.Options.CustomerChangeLeadMinutes = 30;
        var u = await StartsInsideTheLockAsync(ctx);

        var cancelled = await new CancelSurveyAppointmentHandler(u, ctx.Scheduling.Calendar, ctx.Scheduling.Options, ctx.Scheduling.Policies, ctx.Clock)
            .HandleAsync(new CancelSurveyAppointmentCommand(s.Request.Customer.Id, s.Request.Request.Id, null), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, cancelled.ErrorCode);
        ctx.Db.ChangeTracker.Clear();
        await AssertUnchangedAsync(ctx, s);
    }
}
