using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// BL-09: an appointment a Manager dispatched across zones (with a recorded authorization) keeps its authorized technician when it
/// is rescheduled, and the authorization is never carried over to a different technician. Geography is SYNTHETIC.
/// </summary>
public sealed class CrossZoneRescheduleTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record Dispatched(SchedulingTestContext Ctx, SeededSurveyRequest Request, Guid Technician, Guid Manager, Guid AppointmentId, Guid AuthorizationId);

    /// <summary>An uncovered site (Bến Thành) dispatched by a Manager to a District 1 technician at 09:00 on <see cref="Day"/>.</summary>
    private static async Task<Dispatched> DispatchedAsync()
    {
        var ctx = Create();
        var manager = await ctx.SeedStaffAsync("Dispatch manager", RoleCodes.Manager);
        var request = await ctx.SeedSurveyRequestAsync(null, "Uncovered customer", communeCode: BenThanh);
        var tech = await ctx.SeedTechnicianAsync("District 1 tech", District1);
        var created = await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(request.Customer.Id, request.Request.Id, [], "Please call first"), None);
        Assert.True(created.Succeeded, created.Message);
        var assigned = await ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(manager.Id, created.Value!.DispatchRequestId, tech.Id, At(9), true,
            "Nearest free technician", created.Value.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        return new Dispatched(ctx, request, tech.Id, manager.Id, appointment.Id, appointment.CrossZoneAuthorizationId!.Value);
    }

    [Fact]
    public async Task A_customer_reschedule_keeps_the_cross_zone_technician_and_the_authorization()
    {
        var d = await DispatchedAsync();
        await using var _ = d.Ctx;

        var result = await d.Ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(d.Request.Customer.Id, d.Request.Request.Id, At(13), null), None);

        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.Message}");
        var next = await d.Ctx.ActiveAppointmentAsync(d.Request.Request.Id);
        Assert.NotEqual(d.AppointmentId, next.Id);
        Assert.Equal((d.Technician, d.AuthorizationId, At(13).ToUniversalTime()), (next.AssignedTechnicianUserId!.Value, next.CrossZoneAuthorizationId!.Value, next.StartsAt));
        var old = await d.Ctx.AppointmentAsync(d.AppointmentId);
        Assert.Equal((AppointmentStatuses.Cancelled, next.Id), (old.Status, old.ReplacedByAppointmentId!.Value));   // history kept
    }

    [Fact]
    public async Task A_reschedule_to_a_time_the_cross_zone_technician_cannot_take_is_refused_and_changes_nothing()
    {
        var d = await DispatchedAsync();
        await using var _ = d.Ctx;
        // The technician is on leave the next working day.
        var leaveDay = Day.AddDays(1);
        Assert.True((await d.Ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), d.Technician, At(8, 0, leaveDay), At(17, 0, leaveDay), "Leave"), None)).Succeeded);

        var result = await d.Ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(d.Request.Customer.Id, d.Request.Request.Id, At(10, 0, leaveDay), null), None);

        Assert.Equal(SchedulingErrorCodes.TechnicianOnLeave, result.ErrorCode);   // never handed to someone else
        var unchanged = await d.Ctx.ActiveAppointmentAsync(d.Request.Request.Id);
        Assert.Equal((d.AppointmentId, d.Technician), (unchanged.Id, unchanged.AssignedTechnicianUserId!.Value));
    }

    [Fact]
    public async Task Sales_keeps_the_authorized_technician_but_an_explicit_other_technician_gets_no_authorization()
    {
        var d = await DispatchedAsync();
        await using var _ = d.Ctx;
        var owner = await d.Ctx.ClaimAsync(d.Request);
        var other = await d.Ctx.SeedTechnicianAsync("Other District 1 tech", District1);

        var toOther = await d.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(owner.Id, d.Request.Request.Id, At(13), "Customer prefers the afternoon", other.Id), None);
        Assert.False(toOther.Succeeded);
        Assert.Equal(d.AppointmentId, (await d.Ctx.ActiveAppointmentAsync(d.Request.Request.Id)).Id);

        var kept = await d.Ctx.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(owner.Id, d.Request.Request.Id, At(13), "Customer prefers the afternoon", null), None);

        Assert.True(kept.Succeeded, $"{kept.ErrorCode}: {kept.Message}");
        var next = await d.Ctx.ActiveAppointmentAsync(d.Request.Request.Id);
        Assert.Equal((d.Technician, d.AuthorizationId), (next.AssignedTechnicianUserId!.Value, next.CrossZoneAuthorizationId!.Value));
        Assert.Single(await d.Ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());   // nothing new authorized implicitly
    }
}
