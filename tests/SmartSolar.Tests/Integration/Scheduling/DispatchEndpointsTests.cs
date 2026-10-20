using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Uncovered-zone dispatch through the real pipeline (JWT roles, envelope, status codes, persistence): a customer on Phường Sài Gòn
/// (26740, no zone maps it) requests a dispatch, a Manager proposes a technician of zone KHU-BINH-TAN (cross-zone, authorized) and the
/// customer accepts. Plus RBAC and validation of every dispatch endpoint.
/// </summary>
public sealed class DispatchEndpointsTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private const string BinhTanWard = "27442";
    private const string SaiGon = "26740";

    private static string BookingDate() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam).DateTime).AddDays(2).ToString("yyyy-MM-dd");

    private static string Start(int hour = 9) => $"{BookingDate()}T{hour:00}:00:00+07:00";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await BodyOf(r);
        Assert.True(expected == r.StatusCode, $"Expected {expected} but got {(int)r.StatusCode}: {body}");
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        return body.GetProperty("data");
    }

    private static async Task<JsonElement> FailureOf(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        return body.GetProperty("error");
    }

    private sealed record World(AuthApiFactory Factory, HttpClient Admin, HttpClient Manager, Guid ZoneId, Guid TechnicianId);

    private static async Task<World> WorldAsync(AuthApiFactory factory)
    {
        _ = factory.Services;
        await using (var db = factory.CreateDbContext()) await AdministrativeCatalogTestData.SeedCatalogAsync(db);
        var (_, admin) = await factory.SeedUserClientAsync(RoleCodes.Admin, "Admin");
        var (_, manager) = await factory.SeedUserClientAsync(RoleCodes.Manager, "Manager");
        var zone = await DataOf(await admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-BINH-TAN", name = "Khu Bình Tân" }), HttpStatusCode.Created);
        var zoneId = zone.GetProperty("zoneId").GetGuid();
        await DataOf(await admin.PutAsJsonAsync($"/api/admin/operational-zones/{zoneId}/communes", new { communeCodes = new[] { BinhTanWard }, revision = 0 }));
        Guid technicianId;
        await using (var db = factory.CreateDbContext())
        {
            var user = PreSurveyTestContext.NewUser("Binh Tan tech");
            db.UserAccounts.Add(user);
            var role = await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            technicianId = user.Id;
        }
        await DataOf(await admin.PutAsJsonAsync($"/api/admin/technicians/{technicianId}/profile", new { isSchedulingActive = true }));
        await DataOf(await admin.PutAsJsonAsync($"/api/admin/technicians/{technicianId}/zones", new { zoneIds = new[] { zoneId } }));
        return new World(factory, admin, manager, zoneId, technicianId);
    }

    private static async Task<(SeededCustomer Customer, Guid SurveyRequestId)> SubmittedRequestAsync(AuthApiFactory factory, string name, string communeCode)
    {
        var customer = await factory.SeedCustomerWithSiteAsync(name);
        await DataOf(await customer.Client.PutAsJsonAsync($"/api/customers/me/sites/{customer.Site.Id}/administrative-location", new { provinceCode = "79", communeCode }));
        var created = await DataOf(await customer.Client.PostAsJsonAsync("/api/pre-surveys", new
        {
            propertySiteId = customer.Site.Id, totalAreaM2 = 120, usableAreaM2 = 90, tiltDegree = 15, azimuthDegree = 180, hasObstruction = false
        }), HttpStatusCode.Created);
        var submitted = await DataOf(await customer.Client.PostAsync($"/api/pre-surveys/{created.GetProperty("preSurveyId").GetGuid()}/submit", null));
        return (customer, submitted.GetProperty("surveyRequestId").GetGuid());
    }

    [Fact]
    public async Task A_technician_releasing_a_cross_zone_job_sends_it_back_to_the_queue_and_staff_reassign_it_through_the_api()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory, "Uncovered", SaiGon);
        var basePath = $"/api/customers/me/survey-requests/{surveyRequestId}/dispatch-request";
        var dispatchId = (await DataOf(await customer.Client.PostAsJsonAsync(basePath, new { note = "Gate code 42" }), HttpStatusCode.Created))
            .GetProperty("dispatchRequestId").GetGuid();
        var revision = (await DataOf(await world.Manager.GetAsync($"/api/admin/dispatch-requests/{dispatchId}"))).GetProperty("request").GetProperty("revision").GetInt32();
        await DataOf(await world.Manager.PostAsJsonAsync($"/api/admin/dispatch-requests/{dispatchId}/assign", new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(), crossZoneAuthorized = true, reason = "No zone serves Sài Gòn yet", expectedRevision = revision,
        }));
        Guid appointmentId;
        await using (var db = factory.CreateDbContext())
            appointmentId = (await db.TechnicianAppointments.SingleAsync(a => a.SurveyRequestId == surveyRequestId)).Id;

        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, world.TechnicianId);
        var released = await DataOf(await technician.PostAsJsonAsync($"/api/technicians/me/appointments/{appointmentId}/release", new { reason = "Vehicle broke down" }));
        Assert.Equal(AppointmentStatuses.Cancelled, released.GetProperty("appointmentStatus").GetString());

        // Back in the staff queue, same survey request, the customer did nothing.
        var queue = await DataOf(await world.Manager.GetAsync("/api/admin/dispatch-requests?status=open"));
        var item = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal((DispatchReasons.AssignmentReleased, surveyRequestId), (item.GetProperty("reason").GetString(), item.GetProperty("surveyRequestId").GetGuid()));
        var reopenedId = item.GetProperty("dispatchRequestId").GetGuid();
        var detail = await DataOf(await world.Manager.GetAsync($"/api/admin/dispatch-requests/{reopenedId}"));
        Assert.Equal(appointmentId, detail.GetProperty("releasedAppointmentId").GetGuid());
        Assert.Contains(detail.GetProperty("events").EnumerateArray(), e => e.GetProperty("eventType").GetString() == DispatchEventTypes.AssignmentReleased);
        var mine = await DataOf(await customer.Client.GetAsync(basePath));
        Assert.Equal(DispatchStatuses.Open, mine.GetProperty("status").GetString());

        // Reassigned (same slot) by the Admin with explicit approval.
        var reopenedRevision = item.GetProperty("revision").GetInt32();
        await FailureOf(await world.Admin.PostAsJsonAsync($"/api/admin/dispatch-requests/{reopenedId}/assign", new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(), reason = "Back after repair", expectedRevision = reopenedRevision,
        }), HttpStatusCode.Conflict, DispatchErrorCodes.CrossZoneAuthorizationRequired);
        await DataOf(await world.Admin.PostAsJsonAsync($"/api/admin/dispatch-requests/{reopenedId}/assign", new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(), crossZoneAuthorized = true, reason = "Back after repair", expectedRevision = reopenedRevision,
        }));
        await using var check = factory.CreateDbContext();
        var appointments = await check.TechnicianAppointments.Where(a => a.SurveyRequestId == surveyRequestId).ToListAsync();
        Assert.Equal(2, appointments.Count);
        Assert.Single(appointments, a => a.Status == AppointmentStatuses.Assigned);
        Assert.Equal(2, await check.Set<CrossZoneAuthorization>().CountAsync());
    }

    [Fact]
    public async Task An_uncovered_customer_gets_a_cross_zone_proposal_and_accepting_it_books_an_assigned_appointment()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory, "Uncovered", SaiGon);
        var basePath = $"/api/customers/me/survey-requests/{surveyRequestId}/dispatch-request";

        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}"));
        Assert.Equal(CoverageStatuses.NoZoneCoverage, slots.GetProperty("coverageStatus").GetString());
        Assert.True(slots.GetProperty("dispatchAvailable").GetBoolean());
        var booking = await FailureOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", new { startsAt = Start() }),
            HttpStatusCode.Conflict, CoverageStatuses.NoZoneCoverage);   // temporarily uncovered: 409 + dispatch, not 422
        Assert.True(booking.GetProperty("details").GetProperty("dispatchAvailable").GetBoolean());

        var created = await DataOf(await customer.Client.PostAsJsonAsync(basePath, new
        {
            preferredWindows = new[] { new { startsAt = Start(8), endsAt = Start(12) } }, note = "Mornings please",
        }), HttpStatusCode.Created);
        var dispatchId = created.GetProperty("dispatchRequestId").GetGuid();
        Assert.Equal(DispatchStatuses.Open, created.GetProperty("status").GetString());
        var repeat = await DataOf(await customer.Client.PostAsJsonAsync(basePath, new { }), HttpStatusCode.OK);
        Assert.Equal(dispatchId, repeat.GetProperty("dispatchRequestId").GetGuid());
        await FailureOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", new { startsAt = Start() }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.DispatchRequestOpen);

        // Manager queue with SLA indicators, detail, candidates.
        var queue = await DataOf(await world.Manager.GetAsync("/api/admin/dispatch-requests?status=open"));
        var item = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(240, item.GetProperty("sla").GetProperty("reviewSlaMinutes").GetInt32());
        Assert.False(item.GetProperty("sla").GetProperty("overdue").GetBoolean());
        var revision = item.GetProperty("revision").GetInt32();
        var candidates = await DataOf(await world.Manager.GetAsync($"/api/admin/dispatch-requests/{dispatchId}/candidates?startsAt={Uri.EscapeDataString(Start())}"));
        var candidate = candidates.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("technicianUserId").GetGuid() == world.TechnicianId);
        Assert.True(candidate.GetProperty("crossZone").GetBoolean());
        Assert.True(candidate.GetProperty("isEligible").GetBoolean());

        var path = $"/api/admin/dispatch-requests/{dispatchId}/proposals";
        await FailureOf(await world.Manager.PostAsJsonAsync(path, new { technicianUserId = world.TechnicianId, startsAt = Start(), expectedRevision = revision }),
            HttpStatusCode.Conflict, DispatchErrorCodes.CrossZoneAuthorizationRequired);
        var proposed = await DataOf(await world.Manager.PostAsJsonAsync(path, new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(), crossZoneAuthorized = true, reason = "No zone serves Sài Gòn yet", expectedRevision = revision,
        }), HttpStatusCode.Created);
        Assert.Equal(DispatchStatuses.ProposalPending, proposed.GetProperty("request").GetProperty("status").GetString());
        await FailureOf(await world.Manager.PostAsJsonAsync(path, new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(13), crossZoneAuthorized = true, reason = "Second", expectedRevision = revision + 1,
        }), HttpStatusCode.Conflict, DispatchErrorCodes.ProposalAlreadyPending);

        var mine = await DataOf(await customer.Client.GetAsync(basePath));
        var live = mine.GetProperty("liveProposal");
        Assert.Equal("Binh Tan tech", live.GetProperty("technicianName").GetString());
        Assert.DoesNotContain("crossZone", mine.ToString());                              // internal flag never shown to customers
        var proposalId = live.GetProperty("proposalId").GetGuid();
        var accepted = await DataOf(await customer.Client.PostAsJsonAsync($"{basePath}/proposals/{proposalId}/accept", new { expectedRevision = live.GetProperty("revision").GetInt32() }));
        Assert.Equal(DispatchStatuses.Assigned, accepted.GetProperty("status").GetString());
        var again = await DataOf(await customer.Client.PostAsJsonAsync($"{basePath}/proposals/{proposalId}/accept", new { }));
        Assert.Equal(accepted.GetProperty("appointmentId").GetGuid(), again.GetProperty("appointmentId").GetGuid());

        var request = await DataOf(await customer.Client.GetAsync($"/api/customers/me/survey-requests/{surveyRequestId}"));
        Assert.Equal("SCHEDULED", request.GetProperty("request").GetProperty("status").GetString());
        await using var check = factory.CreateDbContext();
        var appointment = await check.TechnicianAppointments.SingleAsync(a => a.SurveyRequestId == surveyRequestId);
        Assert.Equal((AppointmentStatuses.Assigned, AssignmentSources.ManagerDispatch, world.TechnicianId),
            (appointment.Status, appointment.AssignmentSource, appointment.AssignedTechnicianUserId!.Value));
        Assert.NotNull(appointment.CrossZoneAuthorizationId);
    }

    [Fact]
    public async Task Every_dispatch_endpoint_enforces_roles_ownership_and_validation()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory, "Uncovered", SaiGon);
        var (stranger, _) = await SubmittedRequestAsync(factory, "Stranger", BinhTanWard);
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales");
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, world.TechnicianId);
        var anonymous = factory.CreateClient();
        var basePath = $"/api/customers/me/survey-requests/{surveyRequestId}/dispatch-request";
        var dispatch = await DataOf(await customer.Client.PostAsJsonAsync(basePath, new { }), HttpStatusCode.Created);
        var dispatchId = dispatch.GetProperty("dispatchRequestId").GetGuid();
        var admin = $"/api/admin/dispatch-requests/{dispatchId}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(basePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/dispatch-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(basePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync(basePath, new { })).StatusCode);
        foreach (var client in new[] { customer.Client, sales, technician })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/dispatch-requests")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{admin}/close", new { reason = "x" })).StatusCode);
        }
        await FailureOf(await stranger.Client.GetAsync(basePath), HttpStatusCode.NotFound, DispatchErrorCodes.DispatchRequestNotFound);
        await FailureOf(await stranger.Client.PostAsJsonAsync(basePath, new { }), HttpStatusCode.NotFound, SchedulingErrorCodes.SurveyRequestNotFound);
        await FailureOf(await customer.Client.PostAsJsonAsync($"{basePath}/proposals/{Guid.NewGuid()}/accept", new { }), HttpStatusCode.NotFound,
            DispatchErrorCodes.ProposalNotFound);

        await FailureOf(await world.Admin.GetAsync("/api/admin/dispatch-requests?status=EXPIRED"), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await FailureOf(await world.Admin.GetAsync($"{admin}/candidates?startsAt=tomorrow"), HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await FailureOf(await world.Admin.PostAsJsonAsync($"{admin}/assign", new { technicianUserId = world.TechnicianId, startsAt = Start() }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await FailureOf(await world.Admin.PostAsJsonAsync($"{admin}/assign", new { technicianUserId = world.TechnicianId, startsAt = Start(), expectedRevision = 0, crossZoneAuthorized = true }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.AssignmentReasonRequired);
        await FailureOf(await world.Admin.PostAsJsonAsync($"{admin}/close", new { }), HttpStatusCode.BadRequest, DispatchErrorCodes.ReasonRequired);
        await FailureOf(await world.Admin.GetAsync($"/api/admin/dispatch-requests/{Guid.NewGuid()}"), HttpStatusCode.NotFound, DispatchErrorCodes.DispatchRequestNotFound);
        await FailureOf(await customer.Client.PostAsJsonAsync(basePath, new { preferredWindows = new[] { new { startsAt = "soon", endsAt = "later" } } }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);

        // Admin and Manager both act; a direct assignment by the Admin resolves it.
        Assert.Equal(dispatchId, (await DataOf(await world.Admin.GetAsync(admin))).GetProperty("request").GetProperty("dispatchRequestId").GetGuid());
        var assigned = await DataOf(await world.Admin.PostAsJsonAsync($"{admin}/assign", new
        {
            technicianUserId = world.TechnicianId, startsAt = Start(), crossZoneAuthorized = true, reason = "Covering Sài Gòn", expectedRevision = 0,
        }));
        Assert.Equal(DispatchStatuses.Assigned, assigned.GetProperty("request").GetProperty("status").GetString());
        await FailureOf(await world.Manager.PostAsJsonAsync($"{admin}/close", new { reason = "Done" }), HttpStatusCode.Conflict, DispatchErrorCodes.InvalidState);
        await FailureOf(await customer.Client.PostAsJsonAsync($"{basePath}/cancel", new { }), HttpStatusCode.Conflict, DispatchErrorCodes.InvalidState);
    }
}
