using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Customer in-app notifications of survey appointments: written with the appointment change, to the booking customer only;
/// the customer's own actions never notify the customer.
/// </summary>
public sealed class CustomerAppointmentNotificationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<List<AfterSalesNotification>> OfAsync(SchedulingTestContext ctx, Guid user, string type)
        => ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == user && n.Type == type).ToListAsync();

    private static Task<List<AfterSalesNotification>> AllAsync(SchedulingTestContext ctx) => ctx.Db.AfterSalesNotifications.AsNoTracking().ToListAsync();

    private static BookSurveyAppointmentCommand Book(SeededSurveyRequest s, int hour) => new(s.Customer.Id, s.Request.Id, At(hour), null);

    [Fact]
    public async Task Booking_and_the_technicians_confirmation_notify_the_customer_only()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var booked = await ctx.Book().HandleAsync(Book(request, 9), None);
        Assert.True(booked.Succeeded, booked.Message);
        var notification = Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentBooked));
        Assert.Contains("09:00", notification.Message);
        Assert.Contains("waiting for a technician", notification.Message);

        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        var accepted = await ctx.AcceptAsync(tech.Id, appointment.Id);
        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Contains("09:00", Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentConfirmed)).Message);
        var all = await AllAsync(ctx);
        Assert.Equal(2, all.Count);                                                            // the technician gets no customer notification
        Assert.All(all, n => Assert.Equal(request.Customer.Id, n.RecipientUserId));
    }

    [Fact]
    public async Task A_staff_assignment_and_a_sales_reschedule_notify_the_customer()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var owner = await ctx.ClaimAsync(request);
        Assert.True((await ctx.Book().HandleAsync(Book(request, 9), None)).Succeeded);

        var assigned = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, tech.Id, "Fallback"), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.Contains("09:00", Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentConfirmed)).Message);

        var rescheduled = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner.Id, request.Request.Id, At(10), "Customer asked", null), None);
        Assert.True(rescheduled.Succeeded, rescheduled.Message);
        var moved = Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentRescheduled));
        Assert.Contains("09:00", moved.Message);
        Assert.Contains("10:00", moved.Message);
        Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentConfirmed));   // one event, one notification
        Assert.All(await AllAsync(ctx), n => Assert.Equal(request.Customer.Id, n.RecipientUserId));
    }

    [Fact]
    public async Task The_customers_own_cancellation_is_silent_but_a_cancellation_by_the_escalation_sweep_notifies()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        Assert.True((await ctx.Book().HandleAsync(Book(request, 9), None)).Succeeded);
        var cancelled = await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, "Changed plans"), None);
        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Empty(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentCancelled));

        // Another customer's booking that nobody accepts before its start: the sweep cancels it and tells that customer.
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second customer");
        Assert.True((await ctx.Book().HandleAsync(Book(second, 9), None)).Succeeded);
        ctx.Clock.Now = At(9).AddMinutes(1);
        Assert.Equal(1, await ctx.Escalation().CancelNotAcceptedBeforeStartAsync(10, None));
        Assert.Contains("no technician could confirm", Assert.Single(await OfAsync(ctx, second.Customer.Id, CustomerNotificationTypes.SurveyAppointmentCancelled)).Message);
        Assert.Empty(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentCancelled));
    }

    [Fact]
    public async Task A_technician_release_tells_the_customer_the_appointment_is_waiting_for_another_technician()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(a.Id, appointment.Id)).Succeeded);
        Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentConfirmed));

        var released = await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(a.Id, appointment.Id, "Sick"), None);
        Assert.True(released.Succeeded, released.Message);
        var waiting = Assert.Single(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyTechnicianReleased));
        Assert.Contains("09:00", waiting.Message);
        Assert.Contains("looking for another technician", waiting.Message);
        Assert.Empty(await OfAsync(ctx, request.Customer.Id, CustomerNotificationTypes.SurveyAppointmentCancelled));   // the time is kept
        Assert.All(await AllAsync(ctx), n => Assert.Equal(request.Customer.Id, n.RecipientUserId));
    }
}
