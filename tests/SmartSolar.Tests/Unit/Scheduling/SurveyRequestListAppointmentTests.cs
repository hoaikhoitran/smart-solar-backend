using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Sales survey-request lists carry each request's active appointment (null when unbooked), its overdue flag and the official
/// commune name; technician details only for the owning Sales user. Geography is SYNTHETIC.
/// </summary>
public sealed class SurveyRequestListAppointmentTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task My_requests_show_the_appointment_of_every_state_with_overdue_and_commune_name()
    {
        // Arrange: one Sales user owns six requests in different scheduling states; the clock is 12:00 on Day.
        await using var ctx = Create();
        var t1 = await ctx.SeedTechnicianAsync("Tech one", BinhTan);
        var t2 = await ctx.SeedTechnicianAsync("Tech two", BinhTan);
        var sales = await ctx.SeedSalesAsync();
        async Task<SeededSurveyRequest> OwnedAsync(string name)
        {
            var request = await ctx.SeedSurveyRequestAsync(BinhTan, name);
            Assert.True(await ctx.Inner.UnitOfWork.TryClaimSurveyRequestAsync(request.Request.Id, sales.Id, DateTimeOffset.UtcNow, None));
            return request;
        }
        var unbooked = await OwnedAsync("Unbooked");
        var overdue = await OwnedAsync("Overdue");
        var accepted = await OwnedAsync("Accepted");
        var assigned = await OwnedAsync("Assigned");
        var pending = await OwnedAsync("Pending");
        var completed = await OwnedAsync("Completed");
        Assert.True((await ctx.AcceptAsync(t1.Id, (await ctx.BookAsync(overdue, At(10))).Id)).Succeeded);
        Assert.True((await ctx.AcceptAsync(t1.Id, (await ctx.BookAsync(accepted, At(14, 30))).Id)).Succeeded);
        await ctx.BookAsync(assigned, At(13));
        Assert.True((await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, assigned.Request.Id, t2.Id, "Customer asked"), None)).Succeeded);
        await ctx.BookAsync(pending, At(10, 0, Day.AddDays(1)));
        var done = await ctx.BookAsync(completed, At(13, 0, Day.AddDays(1)));
        Assert.True((await ctx.AcceptAsync(t2.Id, done.Id)).Succeeded);
        var completedAt = ctx.Clock.GetUtcNow();
        await ctx.Db.TechnicianAppointments.Where(a => a.Id == done.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.Status, AppointmentStatuses.Completed).SetProperty(a => a.CompletedAt, completedAt));
        ctx.Clock.Now = At(12).ToUniversalTime();
        var communeName = (await ctx.Db.AdministrativeCommunes.SingleAsync(c => c.Code == BinhTanWard)).Name;

        // Act
        var items = (await new GetMySurveyRequestsHandler(ctx.Inner.UnitOfWork, ctx.Clock).HandleAsync(sales.Id, None))
            .ToDictionary(i => i.SurveyRequestId);

        // Assert
        Assert.Equal(6, items.Count);
        Assert.Null(items[unbooked.Request.Id].Appointment);
        Assert.All(items.Values, i => Assert.Equal(communeName, i.CommuneName));
        Assert.Equal((AppointmentStatuses.Accepted, true, t1.Id), Summary(items[overdue.Request.Id]));
        Assert.Equal((AppointmentStatuses.Accepted, false, t1.Id), Summary(items[accepted.Request.Id]));
        Assert.Equal((AppointmentStatuses.Assigned, false, t2.Id), Summary(items[assigned.Request.Id]));
        Assert.Equal((AppointmentStatuses.Completed, false, t2.Id), Summary(items[completed.Request.Id]));
        var open = items[pending.Request.Id].Appointment!;
        Assert.Equal((AppointmentStatuses.PendingAcceptance, false, (Guid?)null), (open.Status, open.IsOverdue, open.TechnicianUserId));
        Assert.NotNull(open.AcceptanceDeadlineAt);
        Assert.Equal("Tech one", items[overdue.Request.Id].Appointment!.TechnicianName);
    }

    [Fact]
    public async Task The_unclaimed_queue_shows_the_appointment_without_technician_details()
    {
        // Arrange: an unclaimed request whose appointment a technician accepted.
        await using var ctx = Create();
        var technician = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(10));
        Assert.True((await ctx.AcceptAsync(technician.Id, appointment.Id)).Succeeded);
        var unbooked = await ctx.SeedSurveyRequestAsync(BinhTan, "Unbooked");

        // Act
        var items = (await new GetPendingSurveyRequestsHandler(ctx.Inner.UnitOfWork, ctx.Clock).HandleAsync(None)).ToDictionary(i => i.SurveyRequestId);

        // Assert
        var summary = items[request.Request.Id].Appointment!;
        Assert.Equal((appointment.Id, AppointmentStatuses.Accepted), (summary.AppointmentId, summary.Status));
        Assert.Equal(((Guid?)null, (string?)null), (summary.TechnicianUserId, summary.TechnicianName));
        Assert.Null(items[unbooked.Request.Id].Appointment);
    }

    private static (string Status, bool IsOverdue, Guid Technician) Summary(MySurveyRequestItem item)
        => (item.Appointment!.Status, item.Appointment.IsOverdue, item.Appointment.TechnicianUserId!.Value);
}
