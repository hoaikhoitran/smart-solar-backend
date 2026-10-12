using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.AdminUsers;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Real PostgreSQL behaviour of the hardening work: recalculation under concurrent changes, Sales
/// reschedule vs reassignment, account changes vs reservations, atomic reassignment rollback, and
/// the recalculation lease. Every caller uses its own DbContext/connection. Travel is SYNTHETIC.
/// </summary>
public sealed class PostgresHardeningConcurrencyTests
{
    private sealed class World(PostgresTestDatabase database)
    {
        public PostgresTestDatabase Database { get; } = database;
        public SchedulingOptions Options { get; } = new();
        public FixedTimeProvider Clock { get; } = new(DefaultNow.ToUniversalTime());
        public FakeTravelTimeEstimator Travel { get; } = new() { Minutes = _ => 20 };
        public WorkingCalendar Calendar => new(Options);

        private AppointmentReservationService Reservations(SchedulingUnitOfWork u) => new(u, new TechnicianAvailabilityEvaluator(Options, Clock), Travel, Options, Clock, Calendar);
        public ScheduleRecalculationService Recalculation(AppDbContext db) => new(new SchedulingUnitOfWork(db), Travel, Calendar, Options, Clock, NullLogger<ScheduleRecalculationService>.Instance);
        public BookSurveyAppointmentHandler Book(AppDbContext db) { var u = new SchedulingUnitOfWork(db); return new(u, Reservations(u), Calendar, Clock); }
        public CancelSurveyAppointmentHandler Cancel(AppDbContext db) { var u = new SchedulingUnitOfWork(db); return new(u, new ScheduleRecalculationService(u, Travel, Calendar, Options, Clock, NullLogger<ScheduleRecalculationService>.Instance), Calendar, Clock); }
        public AssignTechnicianHandler Assign(AppDbContext db)
        {
            var u = new SchedulingUnitOfWork(db);
            return new(u, Reservations(u), new ScheduleRecalculationService(u, Travel, Calendar, Options, Clock, NullLogger<ScheduleRecalculationService>.Instance),
                new TechnicianDayLoader(u, Calendar), Calendar, Options, Clock);
        }
        public SalesRescheduleHandler SalesReschedule(AppDbContext db)
        {
            var u = new SchedulingUnitOfWork(db);
            return new(u, Reservations(u), new ScheduleRecalculationService(u, Travel, Calendar, Options, Clock, NullLogger<ScheduleRecalculationService>.Instance), Calendar, Clock);
        }
        public AdminUserHandlers AdminUsers(AppDbContext db) => new(new AdminUserStore(db), new SecureTokenFactory(), new FakeIntegrationEventPublisher(),
            new ConfiguredEmailDeliveryStatus(false), new AccountSetupOptions(), new EmailVerificationOptions(), new PasswordResetOptions(),
            new FrontendOptions { EmailVerificationUrl = "https://app.test/v", PasswordResetUrl = "https://app.test/r" }, Clock, NullLogger<AdminUserHandlers>.Instance);
    }

    private static async Task<UserAccount> SeedUserAsync(AppDbContext db, string name, string roleCode)
    {
        var user = PreSurveyTestContext.NewUser(name);
        db.UserAccounts.Add(user);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Code == roleCode) ?? db.Roles.Add(new Role { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode }).Entity;
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<UserAccount> SeedTechnicianAsync(AppDbContext db, string name, GeoPoint baseLocation)
    {
        var user = await SeedUserAsync(db, name, RoleCodes.Technician);
        db.TechnicianProfiles.Add(new TechnicianProfile
        {
            UserId = user.Id, BaseLatitude = baseLocation.Latitude, BaseLongitude = baseLocation.Longitude, IsSchedulingActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SeededSurveyRequest> SeedRequestAsync(AppDbContext db, GeoPoint location, string name)
    {
        var user = await SeedUserAsync(db, name, RoleCodes.Customer);
        var customer = PreSurveyTestContext.NewCustomer(user.Id);
        db.Add(customer);
        var site = PreSurveyTestContext.NewPropertySite(customer.Id);
        site.Latitude = location.Latitude;
        site.Longitude = location.Longitude;
        db.Add(site);
        var preSurvey = PreSurveyTestContext.NewPreSurvey(site.Id, PreSurveyStatus.Submitted);
        db.Add(preSurvey);
        var request = PreSurveyTestContext.NewSurveyRequest(preSurvey.Id);
        db.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new SeededSurveyRequest(user, site, preSurvey, request);
    }

    private static async Task BookAsync(World world, SeededSurveyRequest r, DateTimeOffset start)
    {
        await using var db = world.Database.NewContext();
        var result = await world.Book(db).HandleAsync(new BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, start, null), CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
    }

    private static async Task<Guid> ClaimAsync(World world, SeededSurveyRequest r)
    {
        await using var db = world.Database.NewContext();
        var sales = await SeedUserAsync(db, "Sales", RoleCodes.Sales);
        Assert.True(await new PreSurveyUnitOfWork(db).TryClaimSurveyRequestAsync(r.Request.Id, sales.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        return sales.Id;
    }

    /// <summary>Drains every due recalculation (as the background worker would).</summary>
    private static async Task DrainAsync(World world)
    {
        world.Clock.Now = world.Clock.Now.AddHours(2);   // past any backoff, still before the appointments
        await using var db = world.Database.NewContext();
        foreach (var (tech, date) in await new SchedulingUnitOfWork(db).ListDueRecalculationsAsync(world.Clock.Now, 100, CancellationToken.None))
        {
            await using var run = world.Database.NewContext();
            await world.Recalculation(run).RunAsync(tech, date, CancellationToken.None);
        }
    }

    /// <summary>
    /// Invariants that must hold after any interleaving: one active appointment per survey request,
    /// every reservation held by an ACTIVE, non-deleted TECHNICIAN, assignment equals reservation,
    /// at most one current route snapshot per appointment, and (once drained) every current snapshot
    /// describes the real predecessor.
    /// </summary>
    private static async Task AssertConsistentAsync(PostgresTestDatabase database, bool drained)
    {
        await using var db = database.NewContext();
        var active = await db.TechnicianAppointments.AsNoTracking().Where(a => a.Status != AppointmentStatuses.Cancelled).ToListAsync();
        Assert.All(active.GroupBy(a => a.SurveyRequestId), g => Assert.Single(g));

        var technicianRole = await db.Roles.SingleAsync(r => r.Code == RoleCodes.Technician);
        foreach (var a in active)
        {
            var user = await db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == a.ReservedTechnicianUserId);
            Assert.Equal(UserStatus.Active, user.Status);
            Assert.Null(user.DeletedAt);
            Assert.True(await db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == technicianRole.Id));
            if (a.AssignedTechnicianUserId is { } assigned) Assert.Equal(a.ReservedTechnicianUserId, assigned);
            Assert.True(await db.TechnicianTravelEstimates.CountAsync(e => e.AppointmentId == a.Id && e.SupersededAt == null) <= 1);
        }

        if (!drained) return;
        Assert.Empty(await db.TechnicianScheduleRecalculations.AsNoTracking().ToListAsync());
        foreach (var day in active.GroupBy(a => a.ReservedTechnicianUserId))
        {
            var ordered = day.OrderBy(a => a.StartsAt).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = await db.TechnicianTravelEstimates.AsNoTracking().SingleAsync(e => e.AppointmentId == ordered[i].Id && e.SupersededAt == null);
                if (i == 0) Assert.Equal(TravelOriginKinds.Base, current.OriginKind);
                else Assert.Equal(ordered[i - 1].Id, current.OriginAppointmentId);
                Assert.Equal("OK", current.Status);
            }
        }
    }

    [PostgresFact]
    public async Task Booking_while_a_neighbouring_appointment_is_cancelled_keeps_one_current_route_per_leg()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        world.Options.MaxAppointmentsPerTechnicianPerDay = 5;   // the race is about routes, not the daily limit
        SeededSurveyRequest a, b, c, d;
        await using (var db = database.NewContext())
        {
            await SeedTechnicianAsync(db, "Only tech", District1);
            a = await SeedRequestAsync(db, ThuDuc, "A");
            b = await SeedRequestAsync(db, BinhTan, "B");
            c = await SeedRequestAsync(db, District7, "C");
            d = await SeedRequestAsync(db, District1, "D");
        }
        await BookAsync(world, a, At(8));
        await BookAsync(world, b, At(10, 30));
        await BookAsync(world, c, At(13));

        // Make both operations hold their provider calls until the other one is estimating too.
        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Travel.BeforeAnswer = async () =>
        {
            if (Interlocked.Increment(ref arrived) >= 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        await using var dbCancel = database.NewContext();
        await using var dbBook = database.NewContext();
        var cancel = Task.Run(() => world.Cancel(dbCancel).HandleAsync(new CancelSurveyAppointmentCommand(b.Customer.Id, b.Request.Id, null), CancellationToken.None));
        var book = Task.Run(() => world.Book(dbBook).HandleAsync(new BookSurveyAppointmentCommand(d.Customer.Id, d.Request.Id, At(15, 30), null), CancellationToken.None));
        await Task.WhenAll(cancel, book);
        world.Travel.BeforeAnswer = null;

        Assert.True(cancel.Result.Succeeded, cancel.Result.Message);
        Assert.True(book.Result.Succeeded, book.Result.Message);
        await AssertConsistentAsync(database, drained: false);
        await DrainAsync(world);
        await AssertConsistentAsync(database, drained: true);
    }

    [PostgresFact]
    public async Task Sales_reschedule_racing_with_reassignment_lets_exactly_one_win()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest request;
        UserAccount techA, techB;
        await using (var db = database.NewContext())
        {
            techA = await SeedTechnicianAsync(db, "Tech A", District1);
            techB = await SeedTechnicianAsync(db, "Tech B", District7);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
        }
        await BookAsync(world, request, At(9));
        var sales = await ClaimAsync(world, request);
        Guid reserved;
        await using (var db = database.NewContext())
        {
            reserved = (await db.TechnicianAppointments.AsNoTracking().SingleAsync()).ReservedTechnicianUserId;
            Assert.True((await world.Assign(db).HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, reserved), CancellationToken.None)).Succeeded);
        }
        var other = reserved == techA.Id ? techB.Id : techA.Id;

        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Travel.BeforeAnswer = async () =>
        {
            if (Interlocked.Increment(ref arrived) >= 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        await using var dbReschedule = database.NewContext();
        await using var dbAssign = database.NewContext();
        var reschedule = Task.Run(() => world.SalesReschedule(dbReschedule).HandleAsync(
            new SalesRescheduleCommand(sales, request.Request.Id, At(14), "customer asked", null), CancellationToken.None));
        var reassign = Task.Run(() => world.Assign(dbAssign).HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, other), CancellationToken.None));
        await Task.WhenAll(reschedule, reassign);
        world.Travel.BeforeAnswer = null;

        Assert.True(reschedule.Result.Succeeded ^ reassign.Result.Succeeded,
            $"Exactly one should win: reschedule={reschedule.Result.ErrorCode ?? "ok"}, reassign={reassign.Result.ErrorCode ?? "ok"}");
        await using var check = database.NewContext();
        var active = await check.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Status != AppointmentStatuses.Cancelled);
        var surveyRequest = await check.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id);
        Assert.Equal(active.StartsAt, surveyRequest.ScheduledAt);
        Assert.Equal(reschedule.Result.Succeeded ? reserved : other, active.ReservedTechnicianUserId);
        Assert.Equal(reschedule.Result.Succeeded ? At(14).ToUniversalTime() : At(9).ToUniversalTime(), active.StartsAt);
        await DrainAsync(world);
        await AssertConsistentAsync(database, drained: true);
    }

    [PostgresFact]
    public async Task Disabling_a_technician_waits_for_an_in_flight_reservation_and_then_sees_its_work()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        UserAccount tech, admin;
        SeededSurveyRequest request;
        await using (var db = database.NewContext())
        {
            tech = await SeedTechnicianAsync(db, "Tech", District1);
            admin = await SeedUserAsync(db, "Admin", RoleCodes.Admin);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
        }

        // A reservation transaction holds the technician lock (exactly what booking takes) and inserts work.
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT 1 FROM technician_profile WHERE user_id = '{tech.Id}' FOR UPDATE", holder, tx))
            await lockCmd.ExecuteNonQueryAsync();

        await using var dbAdmin = database.NewContext();
        var disable = Task.Run(() => world.AdminUsers(dbAdmin).ChangeStatusAsync(new ChangeUserStatusCommand(admin.Id, tech.Id, "DISABLED", null), CancellationToken.None));
        await Task.Delay(700);
        Assert.False(disable.IsCompleted, "The account change must wait for the technician lock.");

        var start = At(9).ToUniversalTime();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO technician_appointment (id, appointment_type, survey_request_id, property_site_id, starts_at, ends_at, reserved_technician_user_id, status, booked_by_user_id, booked_at, created_at, updated_at, revision) "
            + "VALUES (@id, 'PRE_SURVEY', @sr, @site, @s, @e, @t, 'BOOKED', @c, now(), now(), now(), 0)", holder, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("sr", request.Request.Id);
            insert.Parameters.AddWithValue("site", request.Site.Id);
            insert.Parameters.AddWithValue("s", start);
            insert.Parameters.AddWithValue("e", start.AddMinutes(90));
            insert.Parameters.AddWithValue("t", tech.Id);
            insert.Parameters.AddWithValue("c", request.Customer.Id);
            await insert.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        var result = await disable;
        Assert.Equal(AdminUserErrorCodes.AccountHasActiveWork, result.ErrorCode);
        await AssertConsistentAsync(database, drained: false);
    }

    [PostgresFact]
    public async Task A_booking_estimated_before_the_technician_was_disabled_is_refused_at_commit()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        UserAccount tech, admin;
        SeededSurveyRequest request;
        await using (var db = database.NewContext())
        {
            tech = await SeedTechnicianAsync(db, "Tech", District1);
            admin = await SeedUserAsync(db, "Admin", RoleCodes.Admin);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
        }

        // The account is disabled while the booking is waiting for its route estimate.
        var disabledOnce = 0;
        world.Travel.BeforeAnswer = async () =>
        {
            if (Interlocked.Exchange(ref disabledOnce, 1) == 1) return;
            await using var dbAdmin = database.NewContext();
            var disabled = await world.AdminUsers(dbAdmin).ChangeStatusAsync(new ChangeUserStatusCommand(admin.Id, tech.Id, "DISABLED", null), CancellationToken.None);
            Assert.True(disabled.Succeeded, disabled.Message);
        };

        await using var dbBook = database.NewContext();
        var booking = await world.Book(dbBook).HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), CancellationToken.None);

        Assert.False(booking.Succeeded);
        await using var check = database.NewContext();
        Assert.Empty(await check.TechnicianAppointments.AsNoTracking().ToListAsync());
        await AssertConsistentAsync(database, drained: false);
    }

    [PostgresFact]
    public async Task Failed_reassignment_rolls_back_and_keeps_the_original_technician()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest request, blocker;
        UserAccount techA, techB;
        await using (var db = database.NewContext())
        {
            techA = await SeedTechnicianAsync(db, "Tech A", District1);
            techB = await SeedTechnicianAsync(db, "Tech B", District7);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
            blocker = await SeedRequestAsync(db, District7, "Blocker");
        }
        await BookAsync(world, request, At(9));
        var sales = await ClaimAsync(world, request);
        TechnicianAppointment original;
        await using (var db = database.NewContext())
        {
            original = await db.TechnicianAppointments.AsNoTracking().SingleAsync();
            Assert.True((await world.Assign(db).HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, original.ReservedTechnicianUserId), CancellationToken.None)).Succeeded);
            original = await db.TechnicianAppointments.AsNoTracking().SingleAsync();
        }
        var target = original.ReservedTechnicianUserId == techA.Id ? techB.Id : techA.Id;

        // While the reassignment is estimating, the target technician gets an overlapping job.
        var once = 0;
        world.Travel.BeforeAnswer = async () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 1) return;
            await using var db = database.NewContext();
            db.TechnicianAppointments.Add(new TechnicianAppointment
            {
                Id = Guid.NewGuid(), AppointmentType = AppointmentTypes.PreSurvey, SurveyRequestId = blocker.Request.Id, PropertySiteId = blocker.Site.Id,
                StartsAt = At(8, 30).ToUniversalTime(), EndsAt = At(10).ToUniversalTime(), ReservedTechnicianUserId = target, Status = AppointmentStatuses.Booked,
                BookedByUserId = blocker.Customer.Id, BookedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        };

        await using var dbAssign = database.NewContext();
        var result = await world.Assign(dbAssign).HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, target, "swap"), CancellationToken.None);

        Assert.False(result.Succeeded);
        await using var check = database.NewContext();
        var kept = await check.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == original.Id);
        Assert.Equal(original.ReservedTechnicianUserId, kept.ReservedTechnicianUserId);
        Assert.Equal(original.AssignedTechnicianUserId, kept.AssignedTechnicianUserId);
        Assert.Equal(original.Revision, kept.Revision);
        Assert.Equal(AppointmentStatuses.Assigned, kept.Status);
        Assert.DoesNotContain(await check.TechnicianAppointmentEvents.AsNoTracking().Select(e => e.EventType).ToListAsync(), t => t == AppointmentEventTypes.Reassigned);
        await AssertConsistentAsync(database, drained: false);
    }

    [PostgresFact]
    public async Task Concurrent_workers_process_a_recalculation_request_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest a, b, c;
        UserAccount tech;
        await using (var db = database.NewContext())
        {
            tech = await SeedTechnicianAsync(db, "Tech", District1);
            a = await SeedRequestAsync(db, ThuDuc, "A");
            b = await SeedRequestAsync(db, BinhTan, "B");
            c = await SeedRequestAsync(db, District7, "C");
        }
        await BookAsync(world, a, At(8));
        await BookAsync(world, b, At(10, 30));
        await BookAsync(world, c, At(13));

        world.Travel.FailWith = Modules.Scheduling.Travel.RouteCalculationStatuses.ProviderUnavailable;   // leave the request queued
        await using (var db = database.NewContext())
            Assert.True((await world.Cancel(db).HandleAsync(new CancelSurveyAppointmentCommand(b.Customer.Id, b.Request.Id, null), CancellationToken.None)).Succeeded);
        world.Travel.FailWith = null;
        world.Clock.Now = world.Clock.Now.AddHours(2);

        await using var w1 = database.NewContext();
        await using var w2 = database.NewContext();
        var outcomes = await Task.WhenAll(
            Task.Run(() => world.Recalculation(w1).RunAsync(tech.Id, Day, CancellationToken.None)),
            Task.Run(() => world.Recalculation(w2).RunAsync(tech.Id, Day, CancellationToken.None)));

        Assert.Single(outcomes, o => o == RecalculationOutcome.Completed);
        Assert.Single(outcomes, o => o == RecalculationOutcome.NotDue);
        await AssertConsistentAsync(database, drained: true);
    }
}
