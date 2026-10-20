using SmartSolar.Modules.Identity.Constants;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Real PostgreSQL concurrency behaviour of the self-acceptance lifecycle (exclusion constraint on the capacity
/// holder, partial unique index, technician row locks, revision-checked updates). Each racer runs on its own
/// DbContext/connection; <see cref="PostgresRace.RaceAsync{T}"/> releases them together. Skipped unless
/// SMARTSOLAR_TEST_POSTGRES points at a disposable local server; the schema comes from the EF migrations, so these
/// tests need the migrations up to the administrative catalog / operational zone one (administrative_*, operational_zone*,
/// technician_zone_membership, property_site.commune_code, technician_appointment.operational_zone_id).
/// </summary>
public sealed class PostgresSchedulingConcurrencyTests
{
    private static CancellationToken None => CancellationToken.None;

    /// <summary>A racer's handlers on its own connection (same fixed clock as the seeding context).</summary>
    private static SchedulingTestContext On(AppDbContext db) => CreateOn(db);

    private static TechnicianAppointment Appointment(Guid? holder, SeededSurveyRequest r, int hour, int minute = 0, string status = AppointmentStatuses.PendingAcceptance)
    {
        var start = At(hour, minute).ToUniversalTime();
        return new TechnicianAppointment
        {
            Id = Guid.NewGuid(), AppointmentType = AppointmentTypes.PreSurvey, SurveyRequestId = r.Request.Id, PropertySiteId = r.Site.Id,
            StartsAt = start, EndsAt = start.AddMinutes(90), ReservedTechnicianUserId = holder, Status = status,
            AcceptanceDeadlineAt = start.AddHours(-2),
            EscalatedAt = status == AppointmentStatuses.Escalated ? DateTimeOffset.UtcNow : null,
            BookedByUserId = r.Customer.Id, BookedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            CancelledAt = status == AppointmentStatuses.Cancelled ? DateTimeOffset.UtcNow : null,
        };
    }

    [PostgresFact]
    public async Task Exclusion_constraint_rejects_overlapping_holds_and_ignores_null_holders()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var a = await ctx.SeedSurveyRequestAsync(BinhTan, "A");
        var b = await ctx.SeedSurveyRequestAsync(BinhTan, "B");
        var c = await ctx.SeedSurveyRequestAsync(BinhTan, "C");
        var d = await ctx.SeedSurveyRequestAsync(BinhTan, "D");

        ctx.Db.TechnicianAppointments.Add(Appointment(tech.Id, a, 9));
        await ctx.Db.SaveChangesAsync();
        ctx.Db.TechnicianAppointments.Add(Appointment(tech.Id, b, 10));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.Db.SaveChangesAsync());
        Assert.Equal("23P01", ((PostgresException)ex.InnerException!).SqlState);
        ctx.Db.ChangeTracker.Clear();

        // Escalated appointments without a holder never conflict with anything.
        ctx.Db.TechnicianAppointments.Add(Appointment(null, b, 9, 0, AppointmentStatuses.Escalated));
        ctx.Db.TechnicianAppointments.Add(Appointment(null, c, 9, 0, AppointmentStatuses.Escalated));
        ctx.Db.TechnicianAppointments.Add(Appointment(tech.Id, d, 9, 0, AppointmentStatuses.Cancelled));
        await ctx.Db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Check_constraints_enforce_the_acceptance_lifecycle()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var a = await ctx.SeedSurveyRequestAsync(BinhTan, "A");
        var b = await ctx.SeedSurveyRequestAsync(BinhTan, "B");

        ctx.Db.TechnicianAppointments.Add(Appointment(null, a, 9));                       // PENDING_ACCEPTANCE needs a holder
        var pending = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.Db.SaveChangesAsync());
        Assert.Equal("23514", ((PostgresException)pending.InnerException!).SqlState);
        ctx.Db.ChangeTracker.Clear();

        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var assigned = Appointment(tech.Id, b, 9, 0, AppointmentStatuses.Assigned);
        assigned.AssignedTechnicianUserId = tech.Id;
        assigned.AssignedAt = DateTimeOffset.UtcNow;
        assigned.AssignmentSource = AssignmentSources.SalesAssigned;                       // ASSIGNED without a reason
        ctx.Db.TechnicianAppointments.Add(assigned);
        var noReason = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.Db.SaveChangesAsync());
        Assert.Equal("23514", ((PostgresException)noReason.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Concurrent_sweeps_cancel_each_started_open_appointment_exactly_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var requests = new List<SeededSurveyRequest>();
        var appointments = new List<TechnicianAppointment>();
        for (var i = 0; i < 3; i++)
        {
            await ctx.SeedTechnicianAsync($"Tech {i}", BinhTan);
            var request = await ctx.SeedSurveyRequestAsync(BinhTan, $"Customer {i}");
            requests.Add(request);
            appointments.Add(await ctx.BookAsync(request, At(9)));
        }
        var owner = await ctx.ClaimAsync(requests[0]);
        await ctx.Db.TechnicianAppointments.Where(a => a.Id == appointments[1].Id)        // an ESCALATED one too
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppointmentStatuses.Escalated).SetProperty(a => a.EscalatedAt, DateTimeOffset.UtcNow));
        var startedAt = At(9, 5).ToUniversalTime();

        var processed = await RaceAsync(database, 4, (db, _) =>
        {
            var racer = On(db);
            racer.Clock.Now = startedAt;
            return Unit.Scheduling.SchedulingAuditFixTests.Worker(() => racer.UnitOfWork, racer.Clock).RunOnceAsync(None);
        });

        Assert.Equal(3, processed.Sum());
        await using var check = database.NewContext();
        var ids = appointments.Select(a => a.Id).ToList();
        var stored = await check.TechnicianAppointments.Where(a => ids.Contains(a.Id)).ToListAsync();
        Assert.All(stored, a =>
        {
            Assert.Equal(AppointmentStatuses.Cancelled, a.Status);
            Assert.Equal(CancellationReasons.NotAcceptedBeforeStart, a.CancellationReason);
        });
        var events = await check.TechnicianAppointmentEvents.Where(e => ids.Contains(e.AppointmentId) && e.EventType == AppointmentEventTypes.Cancelled).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Equal(3, events.Select(e => e.AppointmentId).Distinct().Count());
        Assert.Equal(owner.Id, Assert.Single(await check.SchedulingNotifications.Where(n => n.Type == AppointmentEventTypes.Cancelled).ToListAsync()).RecipientUserId);
        var requestIds = requests.Select(r => r.Request.Id).ToList();
        Assert.All(await check.SurveyRequests.Where(r => requestIds.Contains(r.Id)).ToListAsync(), r =>
        {
            Assert.Equal(SurveyRequestStatus.Pending, r.Status);
            Assert.Null(r.ScheduledAt);
        });

        // The request can be booked again right away (the one-active-appointment index no longer blocks it).
        ctx.Clock.Now = startedAt;
        var rebooked = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(requests[0].Customer.Id, requests[0].Request.Id, At(9, date: Day.AddDays(1)), null), None);
        Assert.True(rebooked.Succeeded, rebooked.Message);
    }

    [PostgresFact]
    public async Task Two_customers_racing_for_the_last_technician_never_double_book()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var tech = await ctx.SeedTechnicianAsync("Only tech", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var requests = new[] { first, second };

        var results = await RaceAsync(database, 2, (db, i) => On(db).Book().HandleAsync(
            new BookSurveyAppointmentCommand(requests[i].Customer.Id, requests[i].Request.Id, At(9), null), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ErrorCode is SchedulingErrorCodes.ScheduleConflict or SchedulingErrorCodes.ConcurrencyConflict);
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.ReservedTechnicianUserId == tech.Id && a.Status != AppointmentStatuses.Cancelled).ToListAsync());
    }

    [PostgresFact]
    public async Task Two_technicians_accepting_the_same_appointment_exactly_one_wins()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var technicians = new[] { x.Id, y.Id };

        var results = await RaceAsync(database, 2, (db, i) => On(db).AcceptAsync(technicians[i], appointment.Id));

        var winner = Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ErrorCode == SchedulingErrorCodes.AppointmentAlreadyTaken);
        await using var check = database.NewContext();
        var stored = await check.TechnicianAppointments.SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(AppointmentStatuses.Accepted, stored.Status);
        Assert.Equal(winner.Value!.TechnicianUserId, stored.AssignedTechnicianUserId);
        Assert.Equal(stored.AssignedTechnicianUserId, stored.ReservedTechnicianUserId);
        Assert.Equal(1, await check.TechnicianAppointmentEvents.CountAsync(e => e.AppointmentId == appointment.Id && e.EventType == AppointmentEventTypes.Accepted));
    }

    [PostgresFact]
    public async Task Acceptance_racing_a_sales_assignment_lets_exactly_one_win()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);

        var results = await RaceAsync(database, 2, (db, i) => i == 0
            ? On(db).AcceptAsync(x.Id, appointment.Id)
            : On(db).Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, y.Id, "Customer asked", appointment.Revision), None));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ErrorCode is SchedulingErrorCodes.AppointmentAlreadyTaken or SchedulingErrorCodes.ConcurrencyConflict);
        await using var check = database.NewContext();
        var stored = await check.TechnicianAppointments.SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(results[0].Succeeded ? x.Id : y.Id, stored.AssignedTechnicianUserId);
        Assert.Equal(results[0].Succeeded ? AppointmentStatuses.Accepted : AppointmentStatuses.Assigned, stored.Status);
        Assert.Equal(SurveyRequestStatus.Scheduled, (await check.SurveyRequests.SingleAsync(r => r.Id == request.Request.Id)).Status);
    }

    [PostgresFact]
    public async Task Acceptance_racing_a_cancellation_leaves_a_consistent_outcome()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).AcceptAsync(tech.Id, appointment.Id)).ErrorCode
            : (await On(db).Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), None)).ErrorCode);

        await using var check = database.NewContext();
        var stored = await check.TechnicianAppointments.SingleAsync(a => a.Id == appointment.Id);
        var surveyRequest = await check.SurveyRequests.SingleAsync(r => r.Id == request.Request.Id);
        Assert.Contains(results, r => r is null);
        if (stored.Status == AppointmentStatuses.Cancelled)
        {
            Assert.Null(results[1]);                                                     // the cancellation committed
            Assert.Equal(SurveyRequestStatus.Pending, surveyRequest.Status);             // even when the accept won first
            Assert.True(results[0] is null or SchedulingErrorCodes.InvalidBookingState or SchedulingErrorCodes.AppointmentAlreadyTaken);
        }
        else
        {
            Assert.Equal(AppointmentStatuses.Accepted, stored.Status);
            Assert.Null(results[0]);
            Assert.Equal(SchedulingErrorCodes.ConcurrencyConflict, results[1]);
            Assert.Equal(SurveyRequestStatus.Scheduled, surveyRequest.Status);
        }
    }

    [PostgresFact]
    public async Task Acceptance_racing_a_reschedule_never_leaves_two_active_appointments()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).AcceptAsync(tech.Id, appointment.Id)).ErrorCode
            : (await On(db).Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), None)).ErrorCode);

        await using var check = database.NewContext();
        var active = await check.TechnicianAppointments.SingleAsync(a => a.SurveyRequestId == request.Request.Id && a.Status != AppointmentStatuses.Cancelled);
        var surveyRequest = await check.SurveyRequests.SingleAsync(r => r.Id == request.Request.Id);
        Assert.Contains(results, r => r is null);
        // The request status always agrees with its single active appointment.
        Assert.Equal(AppointmentStatuses.IsConfirmed(active.Status) ? SurveyRequestStatus.Scheduled : SurveyRequestStatus.Pending, surveyRequest.Status);
        if (active.Id == appointment.Id)
        {
            Assert.Equal(AppointmentStatuses.Accepted, active.Status);                   // the reschedule lost
            Assert.NotNull(results[1]);
        }
        else
        {
            Assert.Equal(At(14).ToUniversalTime(), active.StartsAt);                    // the reschedule won (after or instead of the accept)
            if (results[0] is null) Assert.Equal(tech.Id, active.AssignedTechnicianUserId);  // accepted first: technician kept
        }
    }

    [PostgresFact]
    public async Task Acceptance_rehoming_a_hold_and_a_booking_for_the_freed_technician_stay_consistent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);
        var r1 = await ctx.SeedSurveyRequestAsync(BinhTan, "One");
        var r2 = await ctx.SeedSurveyRequestAsync(BinhTan, "Two");
        var r3 = await ctx.SeedSurveyRequestAsync(BinhTan, "Three");
        var held = await ctx.BookAsync(r1, At(9));
        var target = await ctx.BookAsync(r2, At(9, 30));
        var accepter = held.ReservedTechnicianUserId!.Value;

        await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).AcceptAsync(accepter, target.Id)).Succeeded
            : (await On(db).Book().HandleAsync(new BookSurveyAppointmentCommand(r3.Customer.Id, r3.Request.Id, At(9), null), None)).Succeeded);

        await using var check = database.NewContext();
        var live = await check.TechnicianAppointments.Where(a => a.Status != AppointmentStatuses.Cancelled).ToListAsync();
        // Whatever happened, no technician holds two appointments closer than the buffer.
        foreach (var group in live.Where(a => a.ReservedTechnicianUserId != null).GroupBy(a => a.ReservedTechnicianUserId))
        {
            var ordered = group.OrderBy(a => a.StartsAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i - 1].EndsAt.AddMinutes(30) <= ordered[i].StartsAt, $"technician {group.Key} is double-booked");
        }
        Assert.Contains(new[] { x.Id, y.Id }, id => id == accepter);
    }

    [PostgresFact]
    public async Task Sales_reschedule_racing_a_reassignment_lets_exactly_one_win()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(x.Id, appointment.Id)).Succeeded);
        var owner = await ctx.ClaimAsync(request);
        var revision = (await ctx.AppointmentAsync(appointment.Id)).Revision;

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner.Id, request.Request.Id, At(14), "Afternoon", null), None)).ErrorCode
            : (await On(db).Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, y.Id, "Swap", revision), None)).ErrorCode);

        Assert.Single(results, r => r is null);
        await using var check = database.NewContext();
        var active = await check.TechnicianAppointments.SingleAsync(a => a.SurveyRequestId == request.Request.Id && a.Status != AppointmentStatuses.Cancelled);
        if (results[0] is null)
        {
            Assert.NotEqual(appointment.Id, active.Id);                                   // rescheduled, X kept
            Assert.Equal(At(14).ToUniversalTime(), active.StartsAt);
            Assert.Equal(x.Id, active.AssignedTechnicianUserId);
        }
        else
        {
            Assert.Equal(appointment.Id, active.Id);                                      // reassigned, original time
            Assert.Equal(y.Id, active.AssignedTechnicianUserId);
            Assert.Equal(At(9).ToUniversalTime(), active.StartsAt);
        }
        Assert.Equal(active.StartsAt, (await check.SurveyRequests.SingleAsync(r => r.Id == request.Request.Id)).ScheduledAt);
    }

    /// <summary>After an admin change raced a booking or an acceptance, the technician holds or is confirmed on nothing they are no longer eligible for.</summary>
    private static async Task AssertNothingIneligibleAsync(PostgresTestDatabase database, Guid technicianUserId)
    {
        await using var check = database.NewContext();
        await using var ctx = CreateOn(check);
        var live = await check.TechnicianAppointments.AsNoTracking()
            .Where(a => a.Status != AppointmentStatuses.Cancelled && a.Status != AppointmentStatuses.Completed
                        && (a.ReservedTechnicianUserId == technicianUserId || a.AssignedTechnicianUserId == technicianUserId))
            .ToListAsync();
        var eligibility = ctx.Eligibility();
        foreach (var a in live)
        {
            var coverage = await ctx.UnitOfWork.GetSiteCoverageAsync(a.PropertySiteId, None);
            var (_, reason) = await eligibility.CheckAsync(technicianUserId,
                new SmartSolar.Modules.Scheduling.Domain.EligibilityRequest(a.StartsAt, a.EndsAt, coverage.ZoneIds, new HashSet<Guid> { a.Id }), None);
            Assert.True(reason is null, $"technician {technicianUserId} still committed to {a.Id} ({a.Status}) although {reason}");
        }
    }

    [PostgresFact]
    public async Task Disabling_a_technician_racing_their_acceptance_never_leaves_them_confirmed_while_disabled()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var appointment = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan), At(9));

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).AcceptAsync(x.Id, appointment.Id)).ErrorCode
            : (await On(db).Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(x.Id, false, null), None)).ErrorCode);

        Assert.Contains(results, r => r is null);
        await AssertNothingIneligibleAsync(database, x.Id);
    }

    [PostgresFact]
    public async Task Disabling_a_technician_racing_a_booking_never_leaves_them_holding_it()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        await ctx.SeedTechnicianAsync("Other", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), None)).ErrorCode
            : (await On(db).Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(x.Id, false, null), None)).ErrorCode);

        await AssertNothingIneligibleAsync(database, x.Id);
    }

    [PostgresFact]
    public async Task Adding_leave_racing_an_acceptance_never_leaves_the_technician_confirmed_on_leave()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var appointment = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan), At(9));

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).AcceptAsync(x.Id, appointment.Id)).ErrorCode
            : (await On(db).Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), x.Id, At(8), At(12), "Leave"), None)).ErrorCode);

        Assert.Contains(results, r => r is null);
        Assert.Single(results, r => r is null);                                          // the second one sees the first and is refused
        await AssertNothingIneligibleAsync(database, x.Id);
    }

    [PostgresFact]
    public async Task Removing_a_zone_racing_a_booking_or_an_acceptance_never_leaves_the_technician_committed_outside_their_zones()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var x = await ctx.SeedTechnicianInZonesAsync("X", ["Z-BINH-TAN", "Z-D7"]);
        await ctx.SeedTechnicianAsync("Other", BinhTan);
        var pending = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "Pending"), At(13));
        var booking = await ctx.SeedSurveyRequestAsync(BinhTan, "Booking");
        var district7 = await ctx.EnsureZoneAsync("Z-D7");
        var admin = await ctx.SeedStaffAsync("Admin", RoleCodes.Admin);

        await RaceAsync(database, 3, async (db, i) => i switch
        {
            0 => (await On(db).AcceptAsync(x.Id, pending.Id)).ErrorCode,
            1 => (await On(db).Book().HandleAsync(new BookSurveyAppointmentCommand(booking.Customer.Id, booking.Request.Id, At(9), null), None)).ErrorCode,
            _ => (await On(db).Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, x.Id, [district7.Id]), None)).ErrorCode,
        });

        await AssertNothingIneligibleAsync(database, x.Id);
    }

    [PostgresFact]
    public async Task Two_zones_racing_for_one_commune_end_with_exactly_one_active_mapping()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        var a = await ctx.EnsureZoneAsync("RACE-A", []);
        var b = await ctx.EnsureZoneAsync("RACE-B", []);
        var admin = await ctx.ZoneAdminIdAsync();

        var results = await RaceAsync(database, 2, async (db, i) =>
            (await On(db).Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, i == 0 ? a.Id : b.Id, [SaiGon], null, 0), None)).ErrorCode);

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r == SchedulingErrorCodes.CommuneAlreadyMapped);
        await using var check = database.NewContext();
        Assert.Equal(1, await check.OperationalZoneCommunes.CountAsync(m => m.CommuneCode == SaiGon && m.RemovedAt == null));
    }

    [PostgresFact]
    public async Task Removing_a_commune_racing_a_booking_never_leaves_a_booking_in_a_zone_that_lost_the_commune()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var zone = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        var admin = await ctx.ZoneAdminIdAsync();

        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), None)).ErrorCode
            : (await On(db).Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, zone.Id, [BinhTriDong], "shrink", zone.Revision), None)).ErrorCode);

        await using var check = database.NewContext();
        var booked = await check.TechnicianAppointments.AnyAsync(a => a.SurveyRequestId == request.Request.Id && a.Status != AppointmentStatuses.Cancelled);
        var mapped = await check.OperationalZoneCommunes.AnyAsync(m => m.CommuneCode == BinhTanWard && m.ZoneId == zone.Id && m.RemovedAt == null);
        if (booked)
        {
            Assert.True(mapped);                                                          // the removal saw the booking and was refused
            Assert.Equal(SchedulingErrorCodes.CommuneInUse, results[1]);
        }
        else
        {
            Assert.False(mapped);
            Assert.Contains(results[0], new[] { SchedulingErrorCodes.NoZoneCoverage, SchedulingErrorCodes.ScheduleConflict, SchedulingErrorCodes.NoZoneTechnician });
        }
    }

    [PostgresFact]
    public async Task Deactivating_a_zone_racing_a_booking_never_leaves_a_booking_in_an_inactive_zone()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = CreateOn(database.NewContext());
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var zone = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        var actor = await ctx.ZoneAdminIdAsync();
        var results = await RaceAsync(database, 2, async (db, i) => i == 0
            ? (await On(db).Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), None)).ErrorCode
            : (await On(db).Zones().UpdateAsync(new UpdateZoneCommand(actor, zone.Id, zone.Name, false, null, null, zone.Revision), None)).ErrorCode);

        await using var check = database.NewContext();
        var booked = await check.TechnicianAppointments.AnyAsync(a => a.SurveyRequestId == request.Request.Id && a.Status != AppointmentStatuses.Cancelled);
        var active = (await check.OperationalZones.SingleAsync(z => z.Id == zone.Id)).IsActive;
        // Losing the race, the booking fails safely: NO_ZONE_COVERAGE, or (when its pre-read still saw the zone but the deactivated
        // zone's members were no longer candidates) the precise no-capacity code of the re-read coverage.
        Assert.True(booked
                ? active && results[1] == SchedulingErrorCodes.ZoneInUse
                : !active && results[0] is SchedulingErrorCodes.NoZoneCoverage or SchedulingErrorCodes.ScheduleConflict or SchedulingErrorCodes.NoZoneTechnician,
            $"booked={booked} active={active} results={string.Join(",", results)}");
    }
}
