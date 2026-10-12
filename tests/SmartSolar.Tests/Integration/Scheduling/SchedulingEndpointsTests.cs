using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Flow 1 through the real pipeline (JWT roles, validation, envelope, persistence). Travel times
/// come from the SYNTHETIC fake estimator; Vietmap is never called.
/// </summary>
public sealed class SchedulingEndpointsTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    /// <summary>A local date two days ahead, so every slot is in the future whatever the clock says.</summary>
    private static string BookingDate() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam).DateTime).AddDays(2).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await BodyOf(r);
        Assert.True(expected == r.StatusCode, $"Expected {expected} but got {(int)r.StatusCode}: {body}");
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        return body.GetProperty("data");
    }

    private static async Task AssertFailureAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(UserAccount User, HttpClient Client)> SeedTechnicianAsync(AuthApiFactory factory, string name, decimal lat, decimal lon)
    {
        _ = factory.Services;
        await using var db = factory.CreateDbContext();
        var user = PreSurveyTestContext.NewUser(name);
        db.UserAccounts.Add(user);
        var role = await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        db.TechnicianProfiles.Add(new TechnicianProfile
        {
            UserId = user.Id, BaseLatitude = lat, BaseLongitude = lon, IsSchedulingActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (user, CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, user.Id));
    }

    /// <summary>Customer with site → creates and submits a pre-survey through the existing APIs.</summary>
    private static async Task<(SeededCustomer Customer, Guid PreSurveyId, Guid SurveyRequestId)> SubmittedRequestAsync(AuthApiFactory factory, string name = "Customer")
    {
        var customer = await factory.SeedCustomerWithSiteAsync(name);
        var created = await DataOf(await customer.Client.PostAsJsonAsync("/api/pre-surveys", new
        {
            propertySiteId = customer.Site.Id, totalAreaM2 = 120, usableAreaM2 = 90, tiltDegree = 15, azimuthDegree = 180, hasObstruction = false
        }), HttpStatusCode.Created);
        var preSurveyId = created.GetProperty("preSurveyId").GetGuid();
        var submitted = await DataOf(await customer.Client.PostAsync($"/api/pre-surveys/{preSurveyId}/submit", null));
        return (customer, preSurveyId, submitted.GetProperty("surveyRequestId").GetGuid());
    }

    private static async Task<JsonElement> FirstSlotAsync(HttpClient customer, Guid siteId)
    {
        var slots = await DataOf(await customer.GetAsync($"/api/survey-slots?propertySiteId={siteId}&date={BookingDate()}"));
        Assert.Equal("Asia/Ho_Chi_Minh", slots.GetProperty("timeZone").GetString());
        Assert.True(slots.GetProperty("slots").GetArrayLength() > 0, slots.ToString());
        return slots.GetProperty("slots")[0];
    }

    private static object BookingBody(string startsAt, string? note = null) => new { startsAt, note };

    [Fact]
    public async Task Full_flow_1_customer_books_sales_assigns_technician_acknowledges()
    {
        using var factory = new AuthApiFactory();
        var (technician, technicianClient) = await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, preSurveyId, surveyRequestId) = await SubmittedRequestAsync(factory);

        // Customer: slots and immediate booking (end time decided by the server).
        var slot = await FirstSlotAsync(customer.Client, customer.Site.Id);
        var startsAt = slot.GetProperty("startsAt").GetString()!;
        Assert.EndsWith("+07:00", startsAt);
        var booking = await DataOf(await customer.Client.PostAsJsonAsync(
            $"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt, "Call before arriving")), HttpStatusCode.Created);
        var appointmentId = booking.GetProperty("appointmentId").GetGuid();
        Assert.Equal("BOOKED", booking.GetProperty("appointmentStatus").GetString());
        Assert.Equal(DateTimeOffset.Parse(startsAt).AddMinutes(90), booking.GetProperty("endsAt").GetDateTimeOffset());
        Assert.DoesNotContain(technician.Id.ToString(), booking.ToString());

        var mine = await DataOf(await customer.Client.GetAsync("/api/customers/me/survey-requests"));
        Assert.Equal("PENDING", mine[0].GetProperty("status").GetString());
        Assert.False(mine[0].GetProperty("appointment").GetProperty("technicianAssigned").GetBoolean());

        // Sales: still in the existing pending queue, claim, see the appointment, check availability, assign.
        var (sale, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales");
        var pending = await DataOf(await sales.GetAsync("/api/survey-requests/pending"));
        Assert.Contains(pending.EnumerateArray(), x => x.GetProperty("surveyRequestId").GetGuid() == surveyRequestId);
        await DataOf(await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));
        var detail = await DataOf(await sales.GetAsync($"/api/survey-requests/{surveyRequestId}"));
        Assert.Equal(appointmentId, detail.GetProperty("appointment").GetProperty("appointmentId").GetGuid());
        Assert.Equal(technician.Id, detail.GetProperty("appointment").GetProperty("reservedTechnicianUserId").GetGuid());

        var availability = await DataOf(await sales.GetAsync($"/api/technicians/availability?surveyRequestId={surveyRequestId}"));
        Assert.True(availability.GetProperty("technicians")[0].GetProperty("isCurrentReservation").GetBoolean());
        var assigned = await DataOf(await sales.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = technician.Id }));
        Assert.Equal("ASSIGNED", assigned.GetProperty("appointmentStatus").GetString());
        Assert.Equal("SCHEDULED", assigned.GetProperty("surveyRequestStatus").GetString());

        var afterAssign = await DataOf(await sales.GetAsync($"/api/survey-requests/{surveyRequestId}"));
        Assert.Equal(4, afterAssign.GetProperty("status").GetInt32());  // SurveyRequestStatus.Scheduled = 4 (existing numeric enum)
        Assert.Equal(DateTimeOffset.Parse(startsAt).ToUniversalTime(), afterAssign.GetProperty("scheduledAt").GetDateTimeOffset().ToUniversalTime());

        // Technician: sees the job with authorized customer/property/pre-survey data, then acknowledges (end of Flow 1).
        var jobs = await DataOf(await technicianClient.GetAsync("/api/technicians/me/appointments"));
        Assert.Equal(appointmentId, Assert.Single(jobs.EnumerateArray()).GetProperty("appointmentId").GetGuid());
        var job = await DataOf(await technicianClient.GetAsync($"/api/technicians/me/appointments/{appointmentId}"));
        Assert.Equal(customer.User.FullName, job.GetProperty("customer").GetProperty("fullName").GetString());
        Assert.Equal(10.776889m, job.GetProperty("property").GetProperty("latitude").GetDecimal());
        Assert.Equal(preSurveyId, job.GetProperty("preSurvey").GetProperty("preSurveyId").GetGuid());
        Assert.Equal("Call before arriving", job.GetProperty("customerNote").GetString());
        Assert.Equal("VIETMAP", job.GetProperty("inboundTravelEstimate").GetProperty("provider").GetString());

        var ack = await DataOf(await technicianClient.PostAsync($"/api/technicians/me/appointments/{appointmentId}/acknowledge", null));
        Assert.Equal("ACKNOWLEDGED", ack.GetProperty("status").GetString());
        Assert.False(ack.GetProperty("alreadyAcknowledged").GetBoolean());
        var again = await DataOf(await technicianClient.PostAsync($"/api/technicians/me/appointments/{appointmentId}/acknowledge", null));
        Assert.True(again.GetProperty("alreadyAcknowledged").GetBoolean());

        var customerView = await DataOf(await customer.Client.GetAsync($"/api/customers/me/survey-requests/{surveyRequestId}"));
        Assert.Equal("SCHEDULED", customerView.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("Tech A", customerView.GetProperty("request").GetProperty("appointment").GetProperty("assignedTechnicianName").GetString());
        Assert.Equal(DateTimeOffset.Parse(startsAt), customerView.GetProperty("request").GetProperty("scheduledAt").GetDateTimeOffset());
        _ = sale;
    }

    [Fact]
    public async Task Role_boundaries_and_ownership_are_enforced()
    {
        using var factory = new AuthApiFactory();
        var (technician, technicianClient) = await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory, "Owner");
        var (_, _, otherRequestId) = await SubmittedRequestAsync(factory, "Stranger");
        var startsAt = (await FirstSlotAsync(customer.Client, customer.Site.Id)).GetProperty("startsAt").GetString()!;
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technicianClient.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = technician.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.GetAsync("/api/technicians/me/appointments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technicianClient.GetAsync($"/api/technicians/availability?surveyRequestId={surveyRequestId}")).StatusCode);

        // Another customer's request looks like it does not exist.
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{otherRequestId}/booking", BookingBody(startsAt)),
            HttpStatusCode.NotFound, SchedulingErrorCodes.SurveyRequestNotFound);
        await AssertFailureAsync(await customer.Client.GetAsync($"/api/customers/me/survey-requests/{otherRequestId}"),
            HttpStatusCode.NotFound, SchedulingErrorCodes.SurveyRequestNotFound);

        // Sales may only assign requests they claimed.
        await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt)), HttpStatusCode.Created);
        await AssertFailureAsync(await sales.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = technician.Id }),
            HttpStatusCode.Forbidden, PreSurveyErrorCodes.SurveyRequestNotAssigned);

        // A technician cannot read an appointment that is not officially assigned to them.
        await using var db = factory.CreateDbContext();
        var appointmentId = (await db.TechnicianAppointments.SingleAsync()).Id;
        await AssertFailureAsync(await technicianClient.GetAsync($"/api/technicians/me/appointments/{appointmentId}"),
            HttpStatusCode.NotFound, SchedulingErrorCodes.AppointmentNotFound);
    }

    [Fact]
    public async Task Duplicate_booking_retry_is_idempotent_and_a_second_time_is_rejected()
    {
        using var factory = new AuthApiFactory();
        await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory);
        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}"));
        var first = slots.GetProperty("slots")[0].GetProperty("startsAt").GetString()!;
        var second = slots.GetProperty("slots")[3].GetProperty("startsAt").GetString()!;
        var url = $"/api/customers/me/survey-requests/{surveyRequestId}/booking";

        var created = await DataOf(await customer.Client.PostAsJsonAsync(url, BookingBody(first)), HttpStatusCode.Created);
        var retried = await DataOf(await customer.Client.PostAsJsonAsync(url, BookingBody(first)), HttpStatusCode.OK);

        Assert.Equal(created.GetProperty("appointmentId").GetGuid(), retried.GetProperty("appointmentId").GetGuid());
        Assert.False(retried.GetProperty("created").GetBoolean());
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync(url, BookingBody(second)), HttpStatusCode.Conflict, SchedulingErrorCodes.BookingAlreadyExists);
    }

    [Fact]
    public async Task Customer_can_reschedule_and_cancel_before_assignment()
    {
        using var factory = new AuthApiFactory();
        await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory);
        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}"));
        var first = slots.GetProperty("slots")[0].GetProperty("startsAt").GetString()!;
        var later = slots.GetProperty("slots")[6].GetProperty("startsAt").GetString()!;
        var url = $"/api/customers/me/survey-requests/{surveyRequestId}/booking";
        await DataOf(await customer.Client.PostAsJsonAsync(url, BookingBody(first)), HttpStatusCode.Created);

        var moved = await DataOf(await customer.Client.PutAsJsonAsync(url, BookingBody(later)));
        Assert.Equal(DateTimeOffset.Parse(later), moved.GetProperty("startsAt").GetDateTimeOffset());

        var cancelled = await DataOf(await customer.Client.PostAsJsonAsync($"{url}/cancel", new { reason = "Travelling" }));
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());

        var detail = await DataOf(await customer.Client.GetAsync($"/api/customers/me/survey-requests/{surveyRequestId}"));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("request").GetProperty("appointment").ValueKind);
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync($"{url}/cancel", new { }), HttpStatusCode.NotFound, SchedulingErrorCodes.BookingNotFound);
    }

    [Fact]
    public async Task Sales_can_transfer_the_reservation_to_another_technician()
    {
        using var factory = new AuthApiFactory();
        var (techA, _) = await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory);
        var startsAt = (await FirstSlotAsync(customer.Client, customer.Site.Id)).GetProperty("startsAt").GetString()!;
        await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt)), HttpStatusCode.Created);
        var (techB, techBClient) = await SeedTechnicianAsync(factory, "Tech B", 10.7300m, 106.7200m);
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        await DataOf(await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));

        var assigned = await DataOf(await sales.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = techB.Id }));

        Assert.True(assigned.GetProperty("reassigned").GetBoolean());
        Assert.Equal(techB.Id, assigned.GetProperty("technicianUserId").GetGuid());
        Assert.Single((await DataOf(await techBClient.GetAsync("/api/technicians/me/appointments"))).EnumerateArray());
        await using var db = factory.CreateDbContext();
        var appointment = await db.TechnicianAppointments.SingleAsync();
        Assert.Equal(techB.Id, appointment.ReservedTechnicianUserId);
        Assert.NotEqual(techA.Id, appointment.ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Invalid_times_and_provider_failures_return_explicit_errors()
    {
        using var factory = new AuthApiFactory();
        await SeedTechnicianAsync(factory, "Tech A", 10.7800m, 106.6900m);
        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory);
        var url = $"/api/customers/me/survey-requests/{surveyRequestId}/booking";
        var date = BookingDate();

        await AssertFailureAsync(await customer.Client.PostAsJsonAsync(url, BookingBody($"{date}T08:00:00")), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync(url, BookingBody($"{date}T08:10:00+07:00")), HttpStatusCode.BadRequest, SchedulingErrorCodes.InvalidBookingTime);
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync(url, BookingBody($"{date}T11:00:00+07:00")), HttpStatusCode.BadRequest, SchedulingErrorCodes.InvalidBookingTime);
        await AssertFailureAsync(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}"), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);

        factory.Travel.FailWith = RouteCalculationStatuses.QuotaExceeded;
        await AssertFailureAsync(await customer.Client.PostAsJsonAsync(url, BookingBody($"{date}T09:00:00+07:00")),
            HttpStatusCode.ServiceUnavailable, SchedulingErrorCodes.TravelProviderQuotaExceeded);
        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={date}"));
        Assert.Equal(0, slots.GetProperty("slots").GetArrayLength());
        Assert.Equal("UNAVAILABLE", slots.GetProperty("routingStatus").GetString());
    }

    [Fact]
    public async Task Admin_sets_up_a_technician_and_managers_read_the_dashboard_schedule()
    {
        using var factory = new AuthApiFactory();
        _ = factory.Services;
        var technicianUser = PreSurveyTestContext.NewUser("New technician");
        await using (var db = factory.CreateDbContext())
        {
            db.UserAccounts.Add(technicianUser);
            db.UserRoles.Add(new UserRole { UserId = technicianUser.Id, RoleId = (await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician)).Id, AssignedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);

        await AssertFailureAsync(await admin.PutAsJsonAsync($"/api/admin/technicians/{technicianUser.Id}/profile",
            new { baseLatitude = 0, baseLongitude = 0, isSchedulingActive = true }), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        var profile = await DataOf(await admin.PutAsJsonAsync($"/api/admin/technicians/{technicianUser.Id}/profile",
            new { baseLatitude = 10.78, baseLongitude = 106.69, baseLabel = "District 3 office", isSchedulingActive = true }));
        Assert.True(profile.GetProperty("readyForScheduling").GetBoolean());

        var (customer, _, surveyRequestId) = await SubmittedRequestAsync(factory);
        var startsAt = (await FirstSlotAsync(customer.Client, customer.Site.Id)).GetProperty("startsAt").GetString()!;
        await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", BookingBody(startsAt)), HttpStatusCode.Created);

        var schedule = await DataOf(await manager.GetAsync($"/api/technicians/{technicianUser.Id}/schedule?date={BookingDate()}"));
        Assert.Equal(1, schedule.GetProperty("appointmentCount").GetInt32());
        Assert.Equal("OK", schedule.GetProperty("appointments")[0].GetProperty("routingStatus").GetString());
        Assert.True(schedule.GetProperty("estimatedTravelSeconds").GetInt32() > 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.GetAsync($"/api/technicians/{technicianUser.Id}/schedule")).StatusCode);
    }
}
