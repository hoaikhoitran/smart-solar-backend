using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Overdue survey appointments (scheduled end passed, survey not completed): they stay visible to the technician, carry
/// isOverdue, and the Sales owner or a Manager/Admin recovers them by rescheduling to a future time with an audit reason.
/// Geography is SYNTHETIC.
/// </summary>
public sealed class OverdueAppointmentTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record Scenario(SchedulingTestContext Ctx, Guid TechnicianId, Guid OtherTechnicianId, SeededSurveyRequest Request, Guid OwnerId, Guid AppointmentId);

    /// <summary>A 10:00 appointment on <see cref="Day"/> accepted by the technician and claimed by Sales; the clock is 1 minute after its end.</summary>
    private static async Task<Scenario> OverdueAsync()
    {
        var ctx = Create();
        var technician = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var other = await ctx.SeedTechnicianAsync("Other tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(10));
        Assert.True((await ctx.AcceptAsync(technician.Id, appointment.Id)).Succeeded);
        var owner = await ctx.ClaimAsync(request);
        var accepted = await ctx.AppointmentAsync(appointment.Id);
        ctx.Clock.Now = accepted.EndsAt.AddMinutes(1).ToUniversalTime();
        return new Scenario(ctx, technician.Id, other.Id, request, owner.Id, appointment.Id);
    }

    private static DateTimeOffset NextDay(int hour) => At(hour, 0, Day.AddDays(1));

    [Fact]
    public async Task An_overdue_job_stays_in_the_technician_list_and_is_flagged()
    {
        // Arrange
        var s = await OverdueAsync();
        await using var _ = s.Ctx;

        // Act
        var list = await s.Ctx.Jobs().ListAsync(s.TechnicianId, includePast: false, None);
        var detail = await s.Ctx.Jobs().GetAsync(s.TechnicianId, s.AppointmentId, None);

        // Assert
        var item = Assert.Single(list);
        Assert.Equal((s.AppointmentId, true, s.Request.Request.Id), (item.AppointmentId, item.IsOverdue, item.SurveyRequestId));
        Assert.True(detail.Value!.IsOverdue);
        Assert.Empty(await s.Ctx.Jobs().ListAsync(s.OtherTechnicianId, includePast: true, None));   // never another technician's jobs
    }

    [Fact]
    public async Task Completed_past_jobs_are_returned_only_with_include_past_and_are_not_overdue()
    {
        var s = await OverdueAsync();
        await using var _ = s.Ctx;
        await s.Ctx.Db.TechnicianAppointments.Where(a => a.Id == s.AppointmentId)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, AppointmentStatuses.Completed).SetProperty(a => a.CompletedAt, s.Ctx.Clock.GetUtcNow()));

        Assert.Empty(await s.Ctx.Jobs().ListAsync(s.TechnicianId, includePast: false, None));
        var history = Assert.Single(await s.Ctx.Jobs().ListAsync(s.TechnicianId, includePast: true, None));
        Assert.False(history.IsOverdue);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, (await s.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "late", null), None)).ErrorCode);
    }

    [Fact]
    public async Task Sales_recovers_an_overdue_appointment_to_a_future_time_keeping_history_and_the_technician()
    {
        // Arrange
        var s = await OverdueAsync();
        await using var _ = s.Ctx;

        // Act
        var result = await s.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "Technician was ill", null), None);

        // Assert: a new future appointment replaces the overdue one; the old row is cancelled and linked; the reason is audited.
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        Assert.Equal((s.AppointmentId, s.TechnicianId), (result.Value!.PreviousAppointmentId!.Value, result.Value.TechnicianUserId!.Value));
        var old = await s.Ctx.AppointmentAsync(s.AppointmentId);
        Assert.Equal((AppointmentStatuses.Cancelled, CancellationReasons.RescheduledBySales, result.Value.AppointmentId),
            (old.Status, old.CancellationReason, old.ReplacedByAppointmentId!.Value));
        var rescheduled = await s.Ctx.Db.TechnicianAppointmentEvents.SingleAsync(e => e.AppointmentId == result.Value.AppointmentId && e.EventType == AppointmentEventTypes.Rescheduled);
        Assert.Equal("OVERDUE_RECOVERY: Technician was ill", rescheduled.Reason);
        Assert.False(Assert.Single(await s.Ctx.Jobs().ListAsync(s.TechnicianId, None)).IsOverdue);
    }

    [Fact]
    public async Task Recovery_needs_a_reason_and_waits_until_a_running_appointment_has_ended()
    {
        var s = await OverdueAsync();
        await using var _ = s.Ctx;
        var reschedule = s.Ctx.SalesReschedule();

        Assert.Equal(SchedulingErrorCodes.RecoveryReasonRequired,
            (await reschedule.HandleAsync(new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "  ", null), None)).ErrorCode);

        // During the appointment (started, not ended) nothing can be changed.
        s.Ctx.Clock.Now = At(10, 30).ToUniversalTime();
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState,
            (await reschedule.HandleAsync(new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "late", null), None)).ErrorCode);
        Assert.Equal(AppointmentStatuses.Accepted, (await s.Ctx.AppointmentAsync(s.AppointmentId)).Status);
    }

    [Fact]
    public async Task Only_the_sales_owner_or_a_manager_recovers()
    {
        var s = await OverdueAsync();
        await using var _ = s.Ctx;
        var stranger = await s.Ctx.SeedSalesAsync("Other sales");
        var manager = await s.Ctx.SeedStaffAsync("Manager", SmartSolar.Modules.Identity.Constants.RoleCodes.Manager);

        var refused = await s.Ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(stranger.Id, s.Request.Request.Id, NextDay(10), "late", null), None);
        var recovered = await s.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(manager.Id, s.Request.Request.Id, NextDay(10), "Escalated by customer", s.OtherTechnicianId, IsManager: true), None);

        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotOwned, refused.ErrorCode);
        Assert.True(recovered.Succeeded, $"{recovered.ErrorCode}: {recovered.Message}");
        Assert.Equal((s.OtherTechnicianId, AppointmentStatuses.Assigned), (recovered.Value!.TechnicianUserId!.Value, recovered.Value.AppointmentStatus));
        Assert.Contains(await s.Ctx.Db.TechnicianAppointmentEvents.Where(e => e.AppointmentId == recovered.Value.AppointmentId).ToListAsync(),
            e => e.EventType == AppointmentEventTypes.Reassigned && e.PreviousTechnicianUserId == s.TechnicianId);
    }

    [Fact]
    public async Task A_started_on_site_survey_blocks_recovery()
    {
        var s = await OverdueAsync();
        await using var _ = s.Ctx;
        var now = s.Ctx.Clock.GetUtcNow();
        s.Ctx.Db.OnSiteSurveyReports.Add(new OnSiteSurveyReport
        {
            Id = Guid.NewGuid(), SurveyRequestId = s.Request.Request.Id, AppointmentId = s.AppointmentId, TechnicianUserId = s.TechnicianId,
            Status = OnSiteSurveyStatuses.InProgress, StartedAt = now.AddHours(-2), Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        await s.Ctx.Db.SaveChangesAsync();
        s.Ctx.Db.ChangeTracker.Clear();

        var result = await s.Ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "late", null), None);

        Assert.Equal(SchedulingErrorCodes.OnSiteSurveyInProgress, result.ErrorCode);
        Assert.Equal(AppointmentStatuses.Accepted, (await s.Ctx.AppointmentAsync(s.AppointmentId)).Status);
    }

    [Fact]
    public async Task Recovery_rechecks_the_named_technicians_schedule_and_leaves_the_appointment_unchanged_on_conflict()
    {
        // Arrange: the other technician already has a confirmed job at the proposed new time.
        var s = await OverdueAsync();
        await using var _ = s.Ctx;
        s.Ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var busy = await s.Ctx.SeedSurveyRequestAsync(BinhTan, "Busy customer");
        var busyAppointment = await s.Ctx.BookAsync(busy, NextDay(10));
        Assert.True((await s.Ctx.AcceptAsync(s.OtherTechnicianId, busyAppointment.Id)).Succeeded);
        s.Ctx.Clock.Now = (await s.Ctx.AppointmentAsync(s.AppointmentId)).EndsAt.AddMinutes(1).ToUniversalTime();

        // Act
        var result = await s.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(s.OwnerId, s.Request.Request.Id, NextDay(10), "Reassign", s.OtherTechnicianId), None);

        // Assert
        Assert.False(result.Succeeded);
        var unchanged = await s.Ctx.AppointmentAsync(s.AppointmentId);
        Assert.Equal((AppointmentStatuses.Accepted, s.TechnicianId), (unchanged.Status, unchanged.AssignedTechnicianUserId!.Value));
    }
}
