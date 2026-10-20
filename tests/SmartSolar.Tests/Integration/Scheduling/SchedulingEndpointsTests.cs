using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Flow 1 through the real pipeline (JWT roles, validation, envelope, persistence): official catalog + operational zones,
/// booking with a capacity hold, technician self-acceptance, Sales fallback and Manager override. Commune codes are REAL
/// (communes.csv of vn-2025-07-01-hcm-79): 27442 Phường Bình Tân, 27487 Phường Tân Mỹ, 26740 Phường Sài Gòn.
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
        return body.GetProperty("data");
    }

    private static async Task AssertFailureAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private const string BinhTanWard = "27442";
    private const string TanMy = "27487";
    private const string SaiGon = "26740";

    private sealed record World(AuthApiFactory Factory, HttpClient Admin, Guid AreaId);

    /// <summary>The official catalog is seeded (as the CLI import would); Admin creates zone "KHU-BINH-TAN" with Phường Bình Tân through the API.</summary>
    private static async Task<World> WorldAsync(AuthApiFactory factory)
    {
        _ = factory.Services;
        await using (var db = factory.CreateDbContext()) await AdministrativeCatalogTestData.SeedCatalogAsync(db, withLegacyMap: true);
        var (_, admin) = await factory.SeedUserClientAsync(RoleCodes.Admin, "Admin");
        var zone = await DataOf(await admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-BINH-TAN", name = "Khu Bình Tân" }), HttpStatusCode.Created);
        var zoneId = zone.GetProperty("zoneId").GetGuid();
        var detail = await DataOf(await admin.PutAsJsonAsync($"/api/admin/operational-zones/{zoneId}/communes", new { communeCodes = new[] { BinhTanWard }, revision = 0 }));
        Assert.Equal(BinhTanWard, Assert.Single(detail.GetProperty("communes").EnumerateArray()).GetProperty("code").GetString());
        return new World(factory, admin, zoneId);
    }

    [Fact]
    public async Task Zone_structure_is_admin_only_and_managers_change_memberships_only_in_their_zones_through_the_api()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (manager, managerClient) = await factory.SeedUserClientAsync(RoleCodes.Manager, "Responsible manager");
        var (_, otherManagerClient) = await factory.SeedUserClientAsync(RoleCodes.Manager, "Other manager");
        var revision = (await DataOf(await world.Admin.GetAsync($"/api/admin/operational-zones/{world.AreaId}"))).GetProperty("zone").GetProperty("revision").GetInt32();
        await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}",
            new { name = "Khu Bình Tân", responsibleManagerUserId = manager.Id, revision }));
        var foreign = (await DataOf(await world.Admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-THU-DUC", name = "Khu Thủ Đức" }),
            HttpStatusCode.Created)).GetProperty("zoneId").GetGuid();
        var (tech, _) = await TechnicianAsync(world, "Tech");

        // Structure: a Manager (even the responsible one) is refused for create, update/deactivate and commune mapping.
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-X", name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}",
            new { name = "Renamed", isActive = false, revision = revision + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}/communes",
            new { communeCodes = Array.Empty<string>(), revision = revision + 1 })).StatusCode);
        // Reading stays open to Managers.
        await DataOf(await managerClient.GetAsync("/api/admin/operational-zones"));

        // Memberships: the responsible Manager manages their zone; any other zone is out of scope (403 ZONE_SCOPE_FORBIDDEN).
        await DataOf(await managerClient.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/zones", new { zoneIds = new[] { world.AreaId } }));
        await AssertFailureAsync(await managerClient.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/zones", new { zoneIds = new[] { world.AreaId, foreign } }),
            HttpStatusCode.Forbidden, SchedulingErrorCodes.ZoneScopeForbidden);
        await AssertFailureAsync(await otherManagerClient.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/zones", new { zoneIds = Array.Empty<Guid>() }),
            HttpStatusCode.Forbidden, SchedulingErrorCodes.ZoneScopeForbidden);
        // Admin: global authority.
        await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/technicians/{tech.Id}/zones", new { zoneIds = new[] { world.AreaId, foreign } }));

        await using var db = factory.CreateDbContext();
        Assert.Equal(new[] { foreign, world.AreaId }.Order(), (await db.TechnicianZoneMemberships.Where(m => m.TechnicianUserId == tech.Id && m.RemovedAt == null)
            .Select(m => m.ZoneId).ToListAsync()).Order());
        var zone = await db.OperationalZones.SingleAsync(z => z.Id == world.AreaId);
        Assert.Equal(("Khu Bình Tân", true), (zone.Name, zone.IsActive));                   // the Manager changed nothing
    }

    /// <summary>A TECHNICIAN account configured through the admin API (profile + zones).</summary>
    private static async Task<(UserAccount User, HttpClient Client)> TechnicianAsync(World world, string name, params Guid[] areas)
    {
        _ = world.Factory.Services;
        await using (var db = world.Factory.CreateDbContext())
        {
            var user = PreSurveyTestContext.NewUser(name);
            db.UserAccounts.Add(user);
            var role = await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/technicians/{user.Id}/profile", new { isSchedulingActive = true }));
            await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/technicians/{user.Id}/zones", new { zoneIds = areas }));
            return (user, CatalogEndpointsTests.ClientFor(world.Factory, RoleCodes.Technician, user.Id));
        }
    }

    /// <summary>Customer with a site, the site's official location set through the API, and a submitted pre-survey.</summary>
    private static async Task<(SeededCustomer Customer, Guid SurveyRequestId)> SubmittedRequestAsync(AuthApiFactory factory, string name = "Customer",
        string? communeCode = BinhTanWard)
    {
        var customer = await factory.SeedCustomerWithSiteAsync(name);
        if (communeCode is not null)
            await DataOf(await customer.Client.PutAsJsonAsync($"/api/customers/me/sites/{customer.Site.Id}/administrative-location",
                new { provinceCode = "79", communeCode }));
        var created = await DataOf(await customer.Client.PostAsJsonAsync("/api/pre-surveys", new
        {
            propertySiteId = customer.Site.Id, totalAreaM2 = 120, usableAreaM2 = 90, tiltDegree = 15, azimuthDegree = 180, hasObstruction = false
        }), HttpStatusCode.Created);
        var submitted = await DataOf(await customer.Client.PostAsync($"/api/pre-surveys/{created.GetProperty("preSurveyId").GetGuid()}/submit", null));
        return (customer, submitted.GetProperty("surveyRequestId").GetGuid());
    }

    private static async Task<(Guid AppointmentId, string StartsAt)> BookFirstSlotAsync(SeededCustomer customer, Guid surveyRequestId, int slotIndex = 0)
    {
        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}"));
        var startsAt = slots.GetProperty("slots")[slotIndex].GetProperty("startsAt").GetString()!;
        var booking = await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", new { startsAt, note = "Call first" }),
            HttpStatusCode.Created);
        return (booking.GetProperty("appointmentId").GetGuid(), startsAt);
    }

    [Fact]
    public async Task Full_flow_1_customer_books_a_technician_of_the_zone_accepts_and_sales_claims_afterwards()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (technician, technicianClient) = await TechnicianAsync(world, "Tech A", world.AreaId);

        // Public dropdown data (anonymous).
        var communes = await DataOf(await factory.CreateClient().GetAsync("/api/administrative-units/communes?provinceCode=79&q=binh tan"));
        Assert.Equal(BinhTanWard, Assert.Single(communes.EnumerateArray()).GetProperty("code").GetString());

        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory);
        var slots = await DataOf(await customer.Client.GetAsync($"/api/survey-slots?propertySiteId={customer.Site.Id}&date={BookingDate()}"));
        Assert.Equal("KHU-BINH-TAN", slots.GetProperty("zone").GetProperty("code").GetString());
        var startsAt = slots.GetProperty("slots")[0].GetProperty("startsAt").GetString()!;
        var booking = await DataOf(await customer.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{surveyRequestId}/booking", new { startsAt, note = "Call first" }),
            HttpStatusCode.Created);
        var appointmentId = booking.GetProperty("appointmentId").GetGuid();
        Assert.Equal(AppointmentStatuses.PendingAcceptance, booking.GetProperty("appointmentStatus").GetString());
        Assert.Equal(AcceptanceStatuses.WaitingForTechnician, booking.GetProperty("acceptanceStatus").GetString());
        Assert.DoesNotContain(technician.Id.ToString(), booking.ToString());                 // the holder is never exposed

        // Technician: open job in the area (no street or contact data), then accept.
        var open = await DataOf(await technicianClient.GetAsync("/api/technicians/me/open-appointments"));
        var offered = Assert.Single(open.EnumerateArray());
        Assert.True(offered.GetProperty("canAccept").GetBoolean());
        var openDetail = await DataOf(await technicianClient.GetAsync($"/api/technicians/me/open-appointments/{appointmentId}"));
        Assert.DoesNotContain("1 Le Loi", openDetail.ToString());
        Assert.DoesNotContain(customer.User.Email, openDetail.ToString());

        var accepted = await DataOf(await technicianClient.PostAsJsonAsync($"/api/technicians/me/appointments/{appointmentId}/accept",
            new { expectedRevision = offered.GetProperty("revision").GetInt32() }));
        Assert.Equal(AppointmentStatuses.Accepted, accepted.GetProperty("appointmentStatus").GetString());
        Assert.Equal("SCHEDULED", accepted.GetProperty("surveyRequestStatus").GetString());
        var repeat = await DataOf(await technicianClient.PostAsync($"/api/technicians/me/appointments/{appointmentId}/accept", null));
        Assert.False(repeat.GetProperty("changed").GetBoolean());

        var job = await DataOf(await technicianClient.GetAsync($"/api/technicians/me/appointments/{appointmentId}"));
        Assert.Equal(customer.User.FullName, job.GetProperty("customer").GetProperty("fullName").GetString());
        Assert.Equal("Call first", job.GetProperty("customerNote").GetString());
        Assert.Equal(AssignmentSources.SelfAccepted, job.GetProperty("assignmentSource").GetString());
        // The V1 acknowledge step is gone.
        Assert.Equal(HttpStatusCode.NotFound, (await technicianClient.PostAsync($"/api/technicians/me/appointments/{appointmentId}/acknowledge", null)).StatusCode);

        var customerView = await DataOf(await customer.Client.GetAsync($"/api/customers/me/survey-requests/{surveyRequestId}"));
        Assert.Equal("SCHEDULED", customerView.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(AcceptanceStatuses.Confirmed, customerView.GetProperty("request").GetProperty("appointment").GetProperty("acceptanceStatus").GetString());
        Assert.Equal("Tech A", customerView.GetProperty("request").GetProperty("appointment").GetProperty("assignedTechnicianName").GetString());

        // Sales: the scheduled request is still unclaimed; claiming takes ownership without touching the schedule.
        var (sale, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales");
        Assert.Contains((await DataOf(await sales.GetAsync("/api/survey-requests/pending"))).EnumerateArray(), x => x.GetProperty("surveyRequestId").GetGuid() == surveyRequestId);
        await DataOf(await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));
        var detail = await DataOf(await sales.GetAsync($"/api/survey-requests/{surveyRequestId}"));
        Assert.Equal(sale.Id, detail.GetProperty("assignedSaleId").GetGuid());
        Assert.Equal(4, detail.GetProperty("status").GetInt32());                                   // Scheduled
        Assert.Equal(technician.Id, detail.GetProperty("appointment").GetProperty("assignedTechnicianUserId").GetGuid());
        Assert.Equal(AppointmentStatuses.Accepted, detail.GetProperty("appointment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Role_boundaries_and_ownership_are_enforced()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (technician, technicianClient) = await TechnicianAsync(world, "Tech", world.AreaId);
        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory, "Owner");
        var (appointmentId, _) = await BookFirstSlotAsync(customer, surveyRequestId);
        var (_, owner) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Owner sales");
        var (_, stranger) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Stranger sales");
        await DataOf(await owner.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.PostAsync($"/api/technicians/me/appointments/{appointmentId}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technicianClient.GetAsync("/api/admin/operational-zones")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/operational-zones/coverage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Client.PutAsJsonAsync($"/api/admin/technicians/{technician.Id}/zones", new { zoneIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-X", name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/customers/me/sites/{customer.Site.Id}/administrative-location",
            new { provinceCode = "79", communeCode = BinhTanWard })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technicianClient.PutAsJsonAsync($"/api/customers/me/sites/{customer.Site.Id}/administrative-location",
            new { provinceCode = "79", communeCode = BinhTanWard })).StatusCode);
        // The catalog reads are public.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/administrative-units/provinces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/admin/survey-appointments/{appointmentId}/assign-technician",
            new { technicianUserId = technician.Id, reason = "x" })).StatusCode);
        await AssertFailureAsync(await stranger.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = technician.Id, reason = "Mine" }),
            HttpStatusCode.Forbidden, SchedulingErrorCodes.SurveyRequestNotOwned);
        await AssertFailureAsync(await owner.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician", new { technicianUserId = technician.Id }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await AssertFailureAsync(await stranger.GetAsync($"/api/technicians/availability?surveyRequestId={surveyRequestId}"), HttpStatusCode.Forbidden,
            SchedulingErrorCodes.SurveyRequestNotOwned);
    }

    [Fact]
    public async Task Sites_need_a_commune_in_a_zone_and_cannot_change_it_while_booked()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        await TechnicianAsync(world, "Tech", world.AreaId);
        var (noCodes, noCodesRequest) = await SubmittedRequestAsync(factory, "No codes", communeCode: null);
        var (uncovered, uncoveredRequest) = await SubmittedRequestAsync(factory, "Uncovered", communeCode: SaiGon);   // no zone maps it
        var (covered, coveredRequest) = await SubmittedRequestAsync(factory, "Covered");
        var start = $"{BookingDate()}T09:00:00+07:00";

        var noLocationSlots = await noCodes.Client.GetAsync($"/api/survey-slots?propertySiteId={noCodes.Site.Id}&date={BookingDate()}");
        Assert.Equal(HttpStatusCode.OK, noLocationSlots.StatusCode);
        var slotsBody = await noLocationSlots.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SchedulingErrorCodes.SiteLocationRequired, slotsBody.GetProperty("data").GetProperty("coverageStatus").GetString());
        Assert.False(slotsBody.GetProperty("data").GetProperty("dispatchAvailable").GetBoolean());
        await AssertFailureAsync(await noCodes.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{noCodesRequest}/booking", new { startsAt = start }),
            HttpStatusCode.UnprocessableEntity, SchedulingErrorCodes.SiteLocationRequired);
        await AssertFailureAsync(await uncovered.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{uncoveredRequest}/booking", new { startsAt = start }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.NoZoneCoverage);   // valid location, temporarily uncovered: dispatchable
        var location = $"/api/customers/me/sites/{noCodes.Site.Id}/administrative-location";
        await AssertFailureAsync(await noCodes.Client.PutAsJsonAsync(location, new { provinceCode = "79", districtCode = "79.777" }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);                                  // communeCode required
        await AssertFailureAsync(await noCodes.Client.PutAsJsonAsync(location, new { provinceCode = "79", communeCode = "2744" }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await AssertFailureAsync(await noCodes.Client.PutAsJsonAsync(location, new { provinceCode = "01", communeCode = BinhTanWard }),
            HttpStatusCode.UnprocessableEntity, SchedulingErrorCodes.LocationOutOfServiceArea);
        await AssertFailureAsync(await noCodes.Client.PutAsJsonAsync(location, new { provinceCode = "79", communeCode = "00004" }),
            HttpStatusCode.UnprocessableEntity, SchedulingErrorCodes.LocationUnknown);
        Assert.Equal(HttpStatusCode.NotFound, (await noCodes.Client.PutAsJsonAsync($"/api/customers/me/sites/{covered.Site.Id}/administrative-location",
            new { provinceCode = "79", communeCode = TanMy })).StatusCode);                                        // not their site
        var set = await DataOf(await noCodes.Client.PutAsJsonAsync(location, new { provinceCode = "79", communeCode = TanMy }));
        Assert.Equal("NO_ZONE_COVERAGE", set.GetProperty("coverageStatus").GetString());
        Assert.Equal("Phường Tân Mỹ", set.GetProperty("communeName").GetString());

        await DataOf(await covered.Client.PostAsJsonAsync($"/api/customers/me/survey-requests/{coveredRequest}/booking", new { startsAt = start }), HttpStatusCode.Created);
        await AssertFailureAsync(await covered.Client.PutAsJsonAsync($"/api/customers/me/sites/{covered.Site.Id}/administrative-location",
            new { provinceCode = "79", communeCode = TanMy }), HttpStatusCode.Conflict, SchedulingErrorCodes.SiteHasActiveBooking);
        // The old endpoints are gone.
        Assert.Equal(HttpStatusCode.NotFound, (await covered.Client.PutAsJsonAsync($"/api/customers/me/sites/{covered.Site.Id}/administrative-area",
            new { provinceCode = "79" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/service-areas")).StatusCode);
    }

    [Fact]
    public async Task Sales_fallback_manager_override_escalated_queue_and_sales_owner_through_the_api()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var (first, firstClient) = await TechnicianAsync(world, "First", world.AreaId);
        var (second, _) = await TechnicianAsync(world, "Second", world.AreaId);
        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory);
        var (appointmentId, _) = await BookFirstSlotAsync(customer, surveyRequestId);
        var (sale, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Owner");
        await DataOf(await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null));

        var availability = await DataOf(await sales.GetAsync($"/api/technicians/availability?surveyRequestId={surveyRequestId}"));
        Assert.Equal(2, availability.GetProperty("technicians").GetArrayLength());
        Assert.Empty((await DataOf(await sales.GetAsync("/api/survey-requests/escalated"))).EnumerateArray());   // deadline still ahead

        var assigned = await DataOf(await sales.PostAsJsonAsync($"/api/survey-requests/{surveyRequestId}/assign-technician",
            new { technicianUserId = first.Id, reason = "Customer asked for First", expectedRevision = availability.GetProperty("revision").GetInt32() }));
        Assert.Equal(AppointmentStatuses.Assigned, assigned.GetProperty("appointmentStatus").GetString());
        await AssertFailureAsync(await firstClient.PostAsJsonAsync($"/api/technicians/me/appointments/{appointmentId}/release", new { reason = "" }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);

        var (_, manager) = await factory.SeedUserClientAsync(RoleCodes.Manager, "Manager");
        var overridden = await DataOf(await manager.PostAsJsonAsync($"/api/admin/survey-appointments/{appointmentId}/assign-technician",
            new { technicianUserId = second.Id, reason = "First is ill" }));
        Assert.Equal(AssignmentSources.ManagerOverride, overridden.GetProperty("assignmentSource").GetString());
        Assert.True(overridden.GetProperty("reassigned").GetBoolean());

        var (newSale, _) = await factory.SeedUserClientAsync(RoleCodes.Sales, "New owner");   // the seeder stores the SALES role
        var owner = await DataOf(await manager.PutAsJsonAsync($"/api/admin/survey-requests/{surveyRequestId}/sales-owner",
            new { saleUserId = newSale.Id, reason = "Rebalancing" }));
        Assert.Equal(sale.Id, owner.GetProperty("previousSaleUserId").GetGuid());
        await AssertFailureAsync(await manager.PutAsJsonAsync($"/api/admin/survey-requests/{surveyRequestId}/sales-owner", new { saleUserId = newSale.Id }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Admins_manage_zones_coverage_and_memberships_with_conflict_checks()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);
        var other = await DataOf(await world.Admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "KHU-7", name = "Khu 7" }), HttpStatusCode.Created);
        var otherId = other.GetProperty("zoneId").GetGuid();
        await AssertFailureAsync(await world.Admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "khu-7", name = "Again" }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.ZoneCodeExists);
        await AssertFailureAsync(await world.Admin.PostAsJsonAsync("/api/admin/operational-zones", new { code = "khu 7", name = "Bad" }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{otherId}/communes", new { communeCodes = new[] { BinhTanWard }, revision = 0 }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.CommuneAlreadyMapped);
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{otherId}/communes", new { communeCodes = new[] { "99999" }, revision = 0 }),
            HttpStatusCode.UnprocessableEntity, SchedulingErrorCodes.LocationUnknown);
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{otherId}/communes", new { communeCodes = new[] { "7948" }, revision = 0 }),
            HttpStatusCode.BadRequest, SchedulingErrorCodes.ValidationFailed);
        await AssertFailureAsync(await world.Admin.GetAsync($"/api/admin/operational-zones/{Guid.NewGuid()}"), HttpStatusCode.NotFound, SchedulingErrorCodes.ZoneNotFound);
        var withD7 = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{otherId}/communes", new { communeCodes = new[] { TanMy }, revision = 0 }));
        Assert.Equal(1, withD7.GetProperty("zone").GetProperty("communeCount").GetInt32());
        var (technician, technicianClient) = await TechnicianAsync(world, "Tech", world.AreaId);

        var zones = await DataOf(await world.Admin.GetAsync("/api/admin/operational-zones"));
        Assert.False(zones.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "KHU-7").GetProperty("hasAvailableTechnician").GetBoolean());
        Assert.True(zones.EnumerateArray().Single(a => a.GetProperty("code").GetString() == "KHU-BINH-TAN").GetProperty("hasAvailableTechnician").GetBoolean());
        var coverage = await DataOf(await world.Admin.GetAsync("/api/admin/operational-zones/coverage?provinceCode=79"));
        Assert.Equal(166, coverage.GetProperty("unmappedCommuneCount").GetInt32());
        Assert.Equal("KHU-7", Assert.Single(coverage.GetProperty("zonesWithoutSchedulableTechnicians").EnumerateArray()).GetProperty("code").GetString());
        Assert.Single((await DataOf(await world.Admin.GetAsync($"/api/admin/technicians?zoneId={world.AreaId}"))).EnumerateArray());
        Assert.Single((await DataOf(await world.Admin.GetAsync($"/api/admin/operational-zones/{world.AreaId}/technicians"))).EnumerateArray());
        Assert.Equal(BinhTanWard, (await DataOf(await world.Admin.GetAsync($"/api/admin/operational-zones/{world.AreaId}"))).GetProperty("communes")[0].GetProperty("code").GetString());

        var (customer, surveyRequestId) = await SubmittedRequestAsync(factory);
        var (appointmentId, _) = await BookFirstSlotAsync(customer, surveyRequestId);
        await DataOf(await technicianClient.PostAsync($"/api/technicians/me/appointments/{appointmentId}/accept", null));

        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/technicians/{technician.Id}/zones",
            new { zoneIds = new[] { otherId } }), HttpStatusCode.Conflict, SchedulingErrorCodes.TechnicianHasConflictingAppointments);
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}/communes",
            new { communeCodes = Array.Empty<string>(), reason = "retire", revision = 1 }), HttpStatusCode.Conflict, SchedulingErrorCodes.CommuneInUse);
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/technicians/{technician.Id}/profile", new { isSchedulingActive = false }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.TechnicianHasConflictingAppointments);

        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Off", isActive = false, revision = 1 }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.ZoneInUse);
        var area = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Binh Tan District", isActive = true, revision = 1 }));
        Assert.Equal(2, area.GetProperty("revision").GetInt32());
        await AssertFailureAsync(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Stale", isActive = true, revision = 1 }),
            HttpStatusCode.Conflict, SchedulingErrorCodes.ZoneStale);
    }

    [Fact]
    public async Task Updating_a_zone_without_the_flags_keeps_them()
    {
        using var factory = new AuthApiFactory();
        var world = await WorldAsync(factory);   // revision 1 after the commune set

        // A rename only: omitting isActive / isServiceable must not change them.
        var renamed = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Binh Tan (renamed)", revision = 1 }));
        Assert.True(renamed.GetProperty("isActive").GetBoolean());
        Assert.True(renamed.GetProperty("isServiceable").GetBoolean());
        Assert.Equal("Binh Tan (renamed)", renamed.GetProperty("name").GetString());

        var unserviceable = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Binh Tan", isServiceable = false, revision = 2 }));
        Assert.False(unserviceable.GetProperty("isServiceable").GetBoolean());
        var deactivated = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Binh Tan", isActive = false, revision = 3 }));
        Assert.False(deactivated.GetProperty("isActive").GetBoolean());
        Assert.False(deactivated.GetProperty("isServiceable").GetBoolean());
        var stillInactive = await DataOf(await world.Admin.PutAsJsonAsync($"/api/admin/operational-zones/{world.AreaId}", new { name = "Binh Tan again", revision = 4 }));
        Assert.False(stillInactive.GetProperty("isActive").GetBoolean());
    }
}
