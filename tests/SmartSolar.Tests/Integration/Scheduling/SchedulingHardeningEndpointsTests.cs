using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>New Sales and admin scheduling endpoints through the real pipeline. Travel is SYNTHETIC.</summary>
public sealed class SchedulingHardeningEndpointsTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    private static DateOnly BookingDay() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam).DateTime).AddDays(2);

    private static string LocalIso(DateOnly day, int hour, int minute = 0) => $"{day:yyyy-MM-dd}T{hour:00}:{minute:00}:00+07:00";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await BodyOf(r);
        Assert.True(expected == r.StatusCode, $"Expected {expected} but got {(int)r.StatusCode}: {body}");
        return body.GetProperty("data");
    }

    private static async Task AssertFailureAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(Guid Id, HttpClient Client)> TechnicianAsync(AuthApiFactory factory, string name, decimal lat, decimal lon)
    {
        _ = factory.Services;
        await using var db = factory.CreateDbContext();
        var user = PreSurveyTestContext.NewUser(name);
        db.UserAccounts.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = (await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician)).Id, AssignedAt = DateTimeOffset.UtcNow });
        db.TechnicianProfiles.Add(new TechnicianProfile
        {
            UserId = user.Id, BaseLatitude = lat, BaseLongitude = lon, IsSchedulingActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (user.Id, CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, user.Id));
    }

    /// <summary>Customer books 10:00 on <see cref="BookingDay"/>; Sales claims and assigns the reserved technician.</summary>
    private static async Task<(Guid SurveyRequestId, HttpClient Sales, Guid Reserved, SeededCustomer Customer)> AssignedAsync(AuthApiFactory factory)
    {
        var customer = await factory.SeedCustomerWithSiteAsync();
        var created = await DataOf(await customer.Client.PostAsJsonAsync("/api/pre-surveys", new
        {
            propertySiteId = customer.Site.Id, totalAreaM2 = 120, usableAreaM2 = 90, tiltDegree = 15, azimuthDegree = 180, hasObstruction = false
        }), HttpStatusCode.Created);
        var submitted = await DataOf(await customer.Client.PostAsync($"/api/pre-surveys/{created.GetProperty("preSurveyId").GetGuid()}/submit", null));
        var surveyRequestId = submitted.GetProperty("surveyRequestId").GetGuid();
        await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking",
            new { startsAt = LocalIso(BookingDay(), 10) }), HttpStatusCode.Created);

        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales");
        await DataOf(await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));
        await using var db = factory.CreateDbContext();
        var reserved = (await db.TechnicianAppointments.SingleAsync()).ReservedTechnicianUserId;
        await DataOf(await sales.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = reserved }));
        return (surveyRequestId, sales, reserved, customer);
    }

    [Fact]
    public async Task Slots_expose_honest_capacity_fields()
    {
        using var factory = new AuthApiFactory();
        await TechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var customer = await factory.SeedCustomerWithSiteAsync();

        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDay():yyyy-MM-dd}"));
        var slot = slots.GetProperty("slots")[0];

        Assert.True(slot.GetProperty("canBook").GetBoolean());
        Assert.Equal("EXACT", slot.GetProperty("capacityStatus").GetString());
        Assert.Equal(1, slot.GetProperty("knownAvailableTechnicians").GetInt32());
        Assert.False(slot.TryGetProperty("availableCapacity", out _));
    }

    [Fact]
    public async Task Sales_reschedules_then_reassigns_an_acknowledged_job_through_the_api()
    {
        using var factory = new AuthApiFactory();
        var techA = await TechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var techB = await TechnicianAsync(factory, "Tech B", 10.7340m, 106.7220m);
        var (surveyRequestId, sales, reserved, customer) = await AssignedAsync(factory);
        var first = reserved == techA.Id ? techA : techB;
        var second = reserved == techA.Id ? techB : techA;

        await using (var db = factory.CreateDbContext())
        {
            var id = (await db.TechnicianAppointments.SingleAsync()).Id;
            await DataOf(await first.Client.PostAsync($"/api/technicians/me/appointments/{id}/acknowledge", null));
        }

        // Customer can no longer change it; Sales can.
        await AssertFailureAsync(await customer.Client.PutAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking",
            new { startsAt = LocalIso(BookingDay(), 14) }), HttpStatusCode.Conflict, SchedulingErrorCodes.InvalidBookingState);
        var moved = await DataOf(await sales.PutAsJsonAsync($"/api/survey-requests/{surveyRequestId}/reschedule",
            new { startsAt = LocalIso(BookingDay(), 13, 30), reason = "Customer requested another time" }));
        Assert.Equal("ASSIGNED", moved.GetProperty("appointmentStatus").GetString());
        Assert.True(moved.GetProperty("acknowledgementRequired").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse(LocalIso(BookingDay(), 13, 30)), moved.GetProperty("scheduledAt").GetDateTimeOffset());
        var newAppointmentId = moved.GetProperty("appointmentId").GetGuid();

        await DataOf(await first.Client.PostAsync($"/api/technicians/me/appointments/{newAppointmentId}/acknowledge", null));
        var reassigned = await DataOf(await sales.PutAsJsonAsync($"/api/survey-requests/{surveyRequestId}/reassign-technician",
            new { technicianUserId = second.Id, reason = "First technician is sick" }));
        Assert.True(reassigned.GetProperty("reassigned").GetBoolean());
        Assert.True(reassigned.GetProperty("acknowledgementRequired").GetBoolean());

        Assert.Empty((await DataOf(await first.Client.GetAsync("/api/technicians/me/appointments"))).EnumerateArray());
        var ack = await DataOf(await second.Client.PostAsync($"/api/technicians/me/appointments/{newAppointmentId}/acknowledge", null));
        Assert.False(ack.GetProperty("alreadyAcknowledged").GetBoolean());

        // Validation and ownership.
        await AssertFailureAsync(await sales.PutAsJsonAsync($"/api/survey-requests/{surveyRequestId}/reschedule", new { startsAt = "tomorrow" }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        var (_, otherSales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Other");
        await AssertFailureAsync(await otherSales.PutAsJsonAsync($"/api/survey-requests/{surveyRequestId}/reschedule", new { startsAt = LocalIso(BookingDay(), 15) }),
            HttpStatusCode.Forbidden, PreSurveyErrorCodes.SurveyRequestNotAssigned);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.PutAsJsonAsync($"/api/survey-requests/{surveyRequestId}/reschedule",
            new { startsAt = LocalIso(BookingDay(), 15) })).StatusCode);
    }

    [Fact]
    public async Task Admin_and_manager_configure_working_schedule_and_leave_with_conflict_checks()
    {
        using var factory = new AuthApiFactory();
        var tech = await TechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        await AssignedAsync(factory);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        var day = BookingDay();
        var weekday = day.DayOfWeek.ToString().ToUpperInvariant();

        var defaults = await DataOf(await manager.GetAsync($"/api/admin/technicians/{tech.Id}/working-schedule"));
        Assert.True(defaults.GetProperty("usesDefault").GetBoolean());

        // A week without the booked 10:00 slot on that weekday is refused.
        await AssertFailureAsync(await manager.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/working-schedule", new
        {
            useDefault = false,
            periods = new[] { new { day = weekday, start = "13:00", end = "17:00" } },
        }), HttpStatusCode.Conflict, SchedulingErrorCodes.TechnicianHasConflictingAppointments);

        var saved = await DataOf(await manager.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/working-schedule", new
        {
            useDefault = false,
            periods = new[] { new { day = weekday, start = "08:00", end = "12:00" }, new { day = weekday, start = "13:00", end = "16:00" } },
        }));
        Assert.False(saved.GetProperty("usesDefault").GetBoolean());
        Assert.Equal(2, saved.GetProperty("periods").GetArrayLength());

        await AssertFailureAsync(await manager.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/working-schedule", new
        {
            useDefault = false, periods = new[] { new { day = "FUNDAY", start = "08:00", end = "12:00" } },
        }), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);

        // Leave over the booking is refused with the appointment listed; other leave is fine.
        var conflict = await manager.PostAsJsonAsync($"/api/admin/technicians/{tech.Id}/unavailable-periods",
            new { startsAt = LocalIso(day, 9), endsAt = LocalIso(day, 12), reason = "Sick" });
        var body = await BodyOf(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, body.GetProperty("error").GetProperty("details").GetProperty("conflictingAppointments").GetArrayLength());
        await DataOf(await manager.PostAsJsonAsync($"/api/admin/technicians/{tech.Id}/unavailable-periods",
            new { startsAt = LocalIso(day, 13), endsAt = LocalIso(day, 16), reason = "Training" }), HttpStatusCode.Created);
        Assert.Equal(1, (await DataOf(await manager.GetAsync($"/api/admin/technicians/{tech.Id}/unavailable-periods"))).GetArrayLength());

        // Earliest base departure is configurable; vehicle is always motorcycle.
        var profile = await DataOf(await manager.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/profile",
            new { baseLatitude = 10.78m, baseLongitude = 106.69m, isSchedulingActive = true, earliestBaseDeparture = "06:30" }));
        Assert.Equal("06:30", profile.GetProperty("earliestBaseDeparture").GetString());
        Assert.Equal("MOTORCYCLE", profile.GetProperty("vehicleType").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.Client.GetAsync($"/api/admin/technicians/{tech.Id}/working-schedule")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_reports_snapshot_freshness_and_never_counts_superseded_legs()
    {
        using var factory = new AuthApiFactory();
        var tech = await TechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        await AssignedAsync(factory);

        var schedule = await DataOf(await tech.Client.GetAsync($"/api/technicians/me/schedule?date={BookingDay():yyyy-MM-dd}"));
        var appointment = Assert.Single(schedule.GetProperty("appointments").EnumerateArray());
        Assert.Equal("OK", appointment.GetProperty("routingStatus").GetString());
        Assert.Equal("BASE", appointment.GetProperty("expectedOriginKind").GetString());
        Assert.True(appointment.GetProperty("snapshotAgeMinutes").GetInt32() >= 0);
        Assert.Equal("NOT_RETURNED_BY_PROVIDER", appointment.GetProperty("inboundTravel").GetProperty("congestion").GetProperty("status").GetString());
        Assert.Equal(appointment.GetProperty("inboundTravel").GetProperty("durationSeconds").GetInt32(), schedule.GetProperty("estimatedTravelSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("recalculation").ValueKind);
        Assert.EndsWith("+07:00", schedule.GetProperty("earliestBaseDeparture").GetString());
    }
}
