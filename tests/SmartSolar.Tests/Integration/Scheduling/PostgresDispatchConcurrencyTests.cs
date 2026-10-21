using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Real PostgreSQL races of uncovered-zone dispatch (partial unique indexes, FOR UPDATE on dispatch_request, revision tokens,
/// technician → site → zone → dispatch_request → proposal lock order). Each racer runs on its own DbContext/connection. The schema comes
/// from the EF migrations: these tests need the migration that creates dispatch_request, dispatch_proposal, cross_zone_authorization,
/// dispatch_event, dispatch_notification, dispatch_preferred_window and the new technician_appointment columns.
/// </summary>
public sealed class PostgresDispatchConcurrencyTests
{
    private static CancellationToken None => CancellationToken.None;

    private static SchedulingTestContext On(AppDbContext db) => CreateOn(db);

    private sealed record Seeded(SchedulingTestContext Ctx, SeededSurveyRequest Request, Guid Manager, Guid Technician, Guid OtherTechnician);

    private static async Task<Seeded> SeedAsync(PostgresTestDatabase database)
    {
        var ctx = CreateOn(database.NewContext());
        var manager = await ctx.SeedStaffAsync("Manager", RoleCodes.Manager);
        var tech = await ctx.SeedTechnicianAsync("District 1 tech", District1);
        var other = await ctx.SeedTechnicianAsync("District 7 tech", District7);
        var request = await ctx.SeedSurveyRequestAsync(null, "Uncovered", communeCode: BenThanh);
        return new Seeded(ctx, request, manager.Id, tech.Id, other.Id);
    }

    private static async Task<CustomerDispatchView> OpenAsync(Seeded s)
    {
        var created = await s.Ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(s.Request.Customer.Id, s.Request.Request.Id, [], null), None);
        Assert.True(created.Succeeded, created.Message);
        return created.Value!;
    }

    private static async Task<(CustomerDispatchView Dispatch, Guid ProposalId)> ProposedAsync(Seeded s, DateTimeOffset start)
    {
        var dispatch = await OpenAsync(s);
        var proposed = await s.Ctx.Dispatch().ProposeAsync(new DispatchTechnicianCommand(s.Manager, dispatch.DispatchRequestId, s.Technician, start, true, "Cross-zone",
            dispatch.Revision), None);
        Assert.True(proposed.Succeeded, proposed.Message);
        return (dispatch, proposed.Value!.Proposals.Single().ProposalId);
    }

    [PostgresFact]
    public async Task Concurrent_creation_for_one_survey_request_yields_exactly_one_open_dispatch_request()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;

        var results = await RaceAsync(database, 5, (db, _) => On(db).Dispatch()
            .CreateAsync(new CreateDispatchRequestCommand(s.Request.Customer.Id, s.Request.Request.Id, [], null), None));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
        Assert.Single(results.Select(r => r.Value!.DispatchRequestId).Distinct());
        Assert.Single(results, r => r.Value!.Created);
        await using var check = database.NewContext();
        Assert.Equal(1, await check.Set<DispatchRequest>().CountAsync(d => d.SurveyRequestId == s.Request.Request.Id));
    }

    [PostgresFact]
    public async Task Concurrent_assignments_of_one_dispatch_request_book_exactly_one_appointment()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;
        var dispatch = await OpenAsync(s);

        var results = await RaceAsync(database, 2, (db, i) => On(db).Dispatch().AssignAsync(new DispatchTechnicianCommand(s.Manager, dispatch.DispatchRequestId,
            i == 0 ? s.Technician : s.OtherTechnician, At(9 + 4 * i), true, $"Manager {i}", dispatch.Revision), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Contains(results.Single(r => !r.Succeeded).ErrorCode, new[]
        {
            DispatchErrorCodes.InvalidState, DispatchErrorCodes.StaleRevision, SchedulingErrorCodes.BookingAlreadyExists, SchedulingErrorCodes.ConcurrencyConflict,
        });
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.SurveyRequestId == s.Request.Request.Id).ToListAsync());
        var stored = await check.Set<DispatchRequest>().SingleAsync(d => d.Id == dispatch.DispatchRequestId);
        Assert.Equal(DispatchStatuses.Assigned, stored.Status);
        Assert.Single(await check.Set<CrossZoneAuthorization>().Where(a => a.DispatchRequestId == dispatch.DispatchRequestId).ToListAsync());   // the loser's rolled back
    }

    [PostgresFact]
    public async Task Accept_racing_withdraw_ends_in_exactly_one_consistent_outcome()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;
        var (dispatch, proposalId) = await ProposedAsync(s, At(10));

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).Dispatch().AcceptAsync(new RespondToProposalCommand(s.Request.Customer.Id, s.Request.Request.Id, proposalId, null, null), None)).ErrorCode
            : (await On(db).Dispatch().WithdrawProposalAsync(new WithdrawProposalCommand(s.Manager, dispatch.DispatchRequestId, proposalId, "Changed plan"), None)).ErrorCode);

        await using var check = database.NewContext();
        var proposal = await check.Set<DispatchProposal>().SingleAsync(p => p.Id == proposalId);
        var stored = await check.Set<DispatchRequest>().SingleAsync(d => d.Id == dispatch.DispatchRequestId);
        var appointments = await check.TechnicianAppointments.Where(a => a.SurveyRequestId == s.Request.Request.Id).ToListAsync();
        if (proposal.Status == ProposalStatuses.Accepted)
        {
            Assert.Null(results[0]);
            Assert.Equal(DispatchErrorCodes.ProposalNotPending, results[1]);
            Assert.Equal(DispatchStatuses.Assigned, stored.Status);
            Assert.Single(appointments);
        }
        else
        {
            Assert.Equal(ProposalStatuses.Withdrawn, proposal.Status);
            Assert.Null(results[1]);
            Assert.Equal(DispatchErrorCodes.ProposalNotPending, results[0]);
            Assert.Equal(DispatchStatuses.Open, stored.Status);
            Assert.Empty(appointments);
        }
    }

    [PostgresFact]
    public async Task Accept_racing_the_expiry_worker_either_books_or_expires_never_both()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;
        var (dispatch, proposalId) = await ProposedAsync(s, At(9, date: Day.AddDays(3)));
        var expiresAt = (await s.Ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync(p => p.Id == proposalId)).ExpiresAt;

        var results = await RaceAsync(database, 2, async (db, i) =>
        {
            var racer = On(db);
            if (i == 0)
            {
                racer.Clock.Now = expiresAt.AddSeconds(-1);                              // the customer answers just in time…
                return (await racer.Dispatch().AcceptAsync(new RespondToProposalCommand(s.Request.Customer.Id, s.Request.Request.Id, proposalId, null, null), None)).ErrorCode;
            }
            racer.Clock.Now = expiresAt;                                                // …while the worker sees the persisted expiry
            return (await racer.DispatchSweep().ExpireProposalsAsync(50, None)) == 1 ? "EXPIRED_BY_WORKER" : null;
        });

        await using var check = database.NewContext();
        var proposal = await check.Set<DispatchProposal>().SingleAsync(p => p.Id == proposalId);
        var stored = await check.Set<DispatchRequest>().SingleAsync(d => d.Id == dispatch.DispatchRequestId);
        var appointments = await check.TechnicianAppointments.Where(a => a.SurveyRequestId == s.Request.Request.Id).ToListAsync();
        if (proposal.Status == ProposalStatuses.Accepted)
        {
            Assert.Equal((null, (string?)null), (results[0], results[1]));
            Assert.Equal(DispatchStatuses.Assigned, stored.Status);
            Assert.Single(appointments);
        }
        else
        {
            Assert.Equal(ProposalStatuses.Expired, proposal.Status);
            Assert.Equal("EXPIRED_BY_WORKER", results[1]);
            Assert.Contains(results[0], new[] { DispatchErrorCodes.ProposalExpired, DispatchErrorCodes.ProposalNotPending });
            Assert.Equal(DispatchStatuses.Open, stored.Status);                          // never the request
            Assert.Empty(appointments);
        }
    }

    [PostgresFact]
    public async Task A_double_accept_books_one_appointment()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;
        var (dispatch, proposalId) = await ProposedAsync(s, At(10));

        var results = await RaceAsync(database, 3, (db, _) => On(db).Dispatch()
            .AcceptAsync(new RespondToProposalCommand(s.Request.Customer.Id, s.Request.Request.Id, proposalId, null, null), None));

        // Every caller sees the same outcome (idempotent acceptance), at worst a transient concurrency conflict to retry.
        Assert.Contains(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(SchedulingErrorCodes.ConcurrencyConflict, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.SurveyRequestId == s.Request.Request.Id).ToListAsync());
        Assert.Equal(ProposalStatuses.Accepted, (await check.Set<DispatchProposal>().SingleAsync(p => p.Id == proposalId)).Status);
        Assert.Equal(DispatchStatuses.Assigned, (await check.Set<DispatchRequest>().SingleAsync(d => d.Id == dispatch.DispatchRequestId)).Status);
    }

    /// <summary>A cross-zone dispatch assignment released by its technician: the job is back in the queue as a new OPEN request.</summary>
    private static async Task<(Guid ReopenedId, int Revision, Guid ReleasedAppointmentId)> ReleasedAsync(Seeded s)
    {
        var dispatch = await OpenAsync(s);
        var assigned = await s.Ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(s.Manager, dispatch.DispatchRequestId, s.Technician, At(10), true,
            "Cross-zone", dispatch.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        var released = await s.Ctx.Jobs().ReleaseAsync(new Modules.Scheduling.PreSurveyAppointments.ReleaseAppointmentCommand(s.Technician,
            assigned.Value!.AppointmentId!.Value, "Cannot attend"), None);
        Assert.True(released.Succeeded, released.Message);
        s.Ctx.Db.ChangeTracker.Clear();
        var reopened = await s.Ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Status == DispatchStatuses.Open);
        return (reopened.Id, reopened.Revision, assigned.Value.AppointmentId.Value);
    }

    [PostgresFact]
    public async Task A_released_cross_zone_job_reopens_one_dispatch_request_with_its_history_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;

        var (reopenedId, _, releasedId) = await ReleasedAsync(s);

        await using var check = database.NewContext();
        var requests = await check.Set<DispatchRequest>().Where(d => d.SurveyRequestId == s.Request.Request.Id).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.Single(requests, d => d.Status == DispatchStatuses.Assigned && d.AppointmentId == releasedId);
        var reopened = requests.Single(d => d.Id == reopenedId);
        Assert.Equal((DispatchReasons.AssignmentReleased, releasedId), (reopened.Reason, reopened.ReleasedAppointmentId!.Value));
        var appointment = await check.TechnicianAppointments.SingleAsync(a => a.Id == releasedId);
        Assert.Equal((AppointmentStatuses.Cancelled, CancellationReasons.ReleasedToDispatch), (appointment.Status, appointment.CancellationReason));
        Assert.Equal(Modules.PreSurvey.Enums.SurveyRequestStatus.Pending, (await check.SurveyRequests.SingleAsync(r => r.Id == s.Request.Request.Id)).Status);
    }

    [PostgresFact]
    public async Task Concurrent_reassignments_of_a_released_job_book_exactly_one_appointment()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var _ = s.Ctx;
        var third = (await s.Ctx.SeedTechnicianAsync("Third tech", District7)).Id;
        var admin = (await s.Ctx.SeedStaffAsync("Admin", RoleCodes.Admin)).Id;
        var (reopenedId, revision, releasedId) = await ReleasedAsync(s);

        // A Manager and an Admin reassign the same released job at the same time, to different technicians.
        var results = await RaceAsync(database, 2, (db, i) => On(db).Dispatch().AssignAsync(new DispatchTechnicianCommand(i == 0 ? s.Manager : admin, reopenedId,
            i == 0 ? s.OtherTechnician : third, At(10), true, $"Reassign {i}", revision), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Contains(results.Single(r => !r.Succeeded).ErrorCode, new[]
        {
            DispatchErrorCodes.InvalidState, DispatchErrorCodes.StaleRevision, SchedulingErrorCodes.BookingAlreadyExists, SchedulingErrorCodes.ConcurrencyConflict,
        });
        await using var check = database.NewContext();
        var appointments = await check.TechnicianAppointments.Where(a => a.SurveyRequestId == s.Request.Request.Id).ToListAsync();
        Assert.Single(appointments, a => a.Status != AppointmentStatuses.Cancelled);              // no duplicate assignment
        Assert.Single(appointments, a => a.Id == releasedId && a.Status == AppointmentStatuses.Cancelled);   // the released one is preserved
        Assert.Equal(DispatchStatuses.Assigned, (await check.Set<DispatchRequest>().SingleAsync(d => d.Id == reopenedId)).Status);
        Assert.Single(await check.Set<CrossZoneAuthorization>().Where(a => a.DispatchRequestId == reopenedId).ToListAsync());   // the loser's rolled back
    }
}
