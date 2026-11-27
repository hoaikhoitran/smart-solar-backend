using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Rework after a reported completion through the real API (2026-11-25): 409 INSTALLATION_REWORK_SCHEDULE_REQUIRED until a Manager schedules the rework
/// and the technician accepts it; RBAC, stale revisions and invalid transitions. The installation up to "in progress" is built with the production
/// handlers on the API's database (historical dates); every step under test goes through HTTP with the API's own clock.
/// </summary>
public sealed class ReworkScheduleEndpointsTests
{
    private static readonly Dictionary<string, string?> GenerousLimits = new()
    {
        ["RateLimiting:InstallationWrite:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["RateLimiting:InstallationScheduling:PermitLimit"] = "1000", ["RateLimiting:SchedulingRead:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
        ["Scheduling:EscalationWorkerEnabled"] = "false", ["Dispatch:WorkerEnabled"] = "false",
    };

    [Fact]
    public async Task Rework_after_a_reported_completion_needs_a_new_schedule_through_the_api()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        _ = factory.Services;   // schema + seeded roles before seeding the journey
        await using var ctx = InstallationWorkflowTestContext.CreateOn(factory.CreateDbContext());
        var i = await EarlyCompletionSchedulingTests.InProgressAsync(ctx);
        var projectId = i.ProjectId;
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, i.TechnicianId);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, i.Flow.CustomerId);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, i.Flow.Manager.Id);
        var stranger = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician, Guid.NewGuid());
        var technicianRoute = $"/api/technicians/me/installation-projects/{projectId}";

        // 2. Technician reports completion (a stale revision is refused first; customers and other technicians cannot).
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Done", expectedRevision = 0 }),
            HttpStatusCode.Conflict, InstallationErrorCodes.StaleRevision);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Done" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Done" })).StatusCode);
        await AssertFailureAsync(await stranger.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Done" }),
            HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        var reported = await DataOf(await technician.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Array and inverter commissioned." }));
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, reported.GetProperty("status").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, reported.GetProperty("workReportedAt").ValueKind);
        // Invalid transition: a second report while the customer decides.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "again" }),
            HttpStatusCode.Conflict, InstallationErrorCodes.InvalidState);

        // 3. The customer requests rework.
        var photo = await ctx.VerifiedFileAsync(i.Flow.CustomerId, FilePurposes.AcceptanceEvidence);
        var rework = await DataOf(await customer.PostAsJsonAsync($"/api/customers/me/installation-projects/{projectId}/acceptance",
            new { decision = "REQUEST_REWORK", reason = "One panel is cracked", evidenceFileIds = new[] { photo } }));
        Assert.Equal(InstallationStatuses.ReworkRequired, rework.GetProperty("status").GetString());

        // 4–5. Work cannot resume without a new schedule: 409 INSTALLATION_REWORK_SCHEDULE_REQUIRED (evidence and report).
        var evidence = await ctx.VerifiedFileAsync(i.TechnicianId, FilePurposes.InstallationEvidence);
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{technicianRoute}/evidence", new { fileId = evidence, caption = "rework" }),
            HttpStatusCode.Conflict, InstallationErrorCodes.ReworkScheduleRequired);
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Fixed" }),
            HttpStatusCode.Conflict, InstallationErrorCodes.ReworkScheduleRequired);
        Assert.True((await DataOf(await technician.GetAsync(technicianRoute))).GetProperty("reworkScheduleRequired").GetBoolean());

        // 6. A Manager schedules the rework (technicians and customers cannot; stale revision and missing reason refused).
        var staff = await DataOf(await manager.GetAsync($"/api/installation-projects/{projectId}"));
        var revision = staff.GetProperty("revision").GetInt32();
        Assert.False(staff.GetProperty("assignments")[0].GetProperty("holdsSchedule").GetBoolean());
        var date = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date).AddDays(3);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(7));
        object Body(int rev, string? reason) => new { technicianUserId = i.TechnicianId, scheduledStartsAt = start, scheduledEndsAt = start.AddHours(4), reason, expectedRevision = rev };
        var assignmentRoute = $"/api/installation-projects/{projectId}/assignment";
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PutAsJsonAsync(assignmentRoute, Body(revision, "Rework"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsJsonAsync(assignmentRoute, Body(revision, "Rework"))).StatusCode);
        await AssertFailureAsync(await manager.PutAsJsonAsync(assignmentRoute, Body(revision - 1, "Rework")), HttpStatusCode.Conflict, InstallationErrorCodes.StaleRevision);
        await AssertFailureAsync(await manager.PutAsJsonAsync(assignmentRoute, Body(revision, null)), HttpStatusCode.Conflict, InstallationErrorCodes.ReasonRequired);
        var assigned = await DataOf(await manager.PutAsJsonAsync(assignmentRoute, Body(revision, "Rework: replace the cracked panel")));
        Assert.Equal(InstallationStatuses.ReworkRequired, assigned.GetProperty("status").GetString());

        // 7. Before accepting, still refused (not accepted); the technician accepts.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"{technicianRoute}/evidence", new { fileId = evidence, caption = "rework" }),
            HttpStatusCode.Conflict, InstallationErrorCodes.AssignmentNotAccepted);
        await DataOf(await technician.PostAsJsonAsync($"{technicianRoute}/accept", new { }));
        Assert.False((await DataOf(await technician.GetAsync(technicianRoute))).GetProperty("reworkScheduleRequired").GetBoolean());

        // 8. The next valid work actions succeed; the history keeps the replaced assignment.
        await DataOf(await technician.PostAsJsonAsync($"{technicianRoute}/evidence", new { fileId = evidence, caption = "rework" }));
        var again = await DataOf(await technician.PostAsJsonAsync($"{technicianRoute}/completion-report", new { completionReport = "Panel replaced." }));
        Assert.Equal(InstallationStatuses.PendingCustomerAcceptance, again.GetProperty("status").GetString());
        var history = (await DataOf(await manager.GetAsync($"/api/installation-projects/{projectId}"))).GetProperty("assignments").EnumerateArray()
            .Select(a => a.GetProperty("status").GetString()).OrderBy(s => s).ToList();
        Assert.Equal(new[] { AssignmentStatuses.Accepted, AssignmentStatuses.Replaced }, history);
    }

    [Fact]
    public async Task The_schedule_conflict_queue_is_manager_only_and_validates_its_filter()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        const string route = "/api/manager/scheduling/schedule-conflicts";
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(route)).StatusCode);
        foreach (var role in new[] { RoleCodes.Technician, RoleCodes.Sales, RoleCodes.Customer })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).GetAsync(route)).StatusCode);
        foreach (var role in new[] { RoleCodes.Manager, RoleCodes.Admin })
            Assert.Equal(System.Text.Json.JsonValueKind.Array, (await DataOf(await CatalogEndpointsTests.ClientFor(factory, role).GetAsync($"{route}?status=RESOLVED"))).ValueKind);
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager).GetAsync($"{route}?status=MAYBE"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }
}
