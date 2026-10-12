using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.Entities;
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
/// Real PostgreSQL concurrency behaviour (exclusion constraint, partial unique index, row locks).
/// Each concurrent caller uses its own DbContext/connection. Travel times are SYNTHETIC.
/// </summary>
public sealed class PostgresSchedulingConcurrencyTests
{
    private sealed class World(PostgresTestDatabase database)
    {
        public PostgresTestDatabase Database { get; } = database;
        public SchedulingOptions Options { get; } = new();
        public FixedTimeProvider Clock { get; } = new(DefaultNow.ToUniversalTime());
        public FakeTravelTimeEstimator Travel { get; } = new();
        public WorkingCalendar Calendar => new(Options);

        private AppointmentReservationService Reservations(SchedulingUnitOfWork uow)
            => new(uow, new TechnicianAvailabilityEvaluator(Options, Clock), Travel, Options, Clock, Calendar);

        public ScheduleRecalculationService Recalculation(SchedulingUnitOfWork uow)
            => new(uow, Travel, Calendar, Options, Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduleRecalculationService>.Instance);

        public BookSurveyAppointmentHandler Book(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Reservations(uow), Calendar, Clock);
        }

        public RescheduleSurveyAppointmentHandler Reschedule(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Reservations(uow), Recalculation(uow), Calendar, Clock);
        }

        public AssignTechnicianHandler Assign(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Reservations(uow), Recalculation(uow), new TechnicianDayLoader(uow, Calendar), Calendar, Options, Clock);
        }

        public CancelSurveyAppointmentHandler Cancel(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Recalculation(uow), Calendar, Clock);
        }

        public SalesRescheduleHandler SalesReschedule(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Reservations(uow), Recalculation(uow), Calendar, Clock);
        }

        public AdminTechnicianHandlers Admin(AppDbContext db)
        {
            var uow = new SchedulingUnitOfWork(db);
            return new(uow, Recalculation(uow), Calendar, Clock);
        }
    }

    private static async Task<UserAccount> SeedUserAsync(AppDbContext db, string name, string? roleCode = null)
    {
        var user = PreSurveyTestContext.NewUser(name);
        db.UserAccounts.Add(user);
        if (roleCode is not null)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Code == roleCode)
                       ?? db.Roles.Add(new Role { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode }).Entity;
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        }
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

    /// <summary>Holds every estimator call until <paramref name="parties"/> callers are waiting, so they all read before anyone writes.</summary>
    private static void HoldUntil(FakeTravelTimeEstimator travel, int parties)
    {
        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        travel.BeforeAnswer = async () =>
        {
            if (Interlocked.Increment(ref arrived) >= parties) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
    }

    private static TechnicianAppointment Appointment(Guid technician, SeededSurveyRequest r, int hour, int minute = 0, string status = AppointmentStatuses.Booked)
    {
        var start = At(hour, minute).ToUniversalTime();
        return new TechnicianAppointment
        {
            Id = Guid.NewGuid(), AppointmentType = AppointmentTypes.PreSurvey, SurveyRequestId = r.Request.Id, PropertySiteId = r.Site.Id,
            StartsAt = start, EndsAt = start.AddMinutes(90), ReservedTechnicianUserId = technician, Status = status,
            BookedByUserId = r.Customer.Id, BookedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            CancelledAt = status == AppointmentStatuses.Cancelled ? DateTimeOffset.UtcNow : null,
        };
    }

    [PostgresFact]
    public async Task Exclusion_constraint_rejects_overlapping_active_appointments_for_one_technician()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        var tech = await SeedTechnicianAsync(db, "Tech", District1);
        var other = await SeedTechnicianAsync(db, "Other", District1);
        var a = await SeedRequestAsync(db, BinhTan, "A");
        var b = await SeedRequestAsync(db, BinhTan, "B");
        var c = await SeedRequestAsync(db, BinhTan, "C");
        var d = await SeedRequestAsync(db, BinhTan, "D");

        db.TechnicianAppointments.Add(Appointment(tech.Id, a, 9));
        await db.SaveChangesAsync();

        db.TechnicianAppointments.Add(Appointment(tech.Id, b, 10));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23P01", ((PostgresException)ex.InnerException!).SqlState);
        db.ChangeTracker.Clear();

        db.TechnicianAppointments.Add(Appointment(tech.Id, b, 10, 30));                      // touching 10:30 is allowed
        db.TechnicianAppointments.Add(Appointment(tech.Id, c, 9, 0, AppointmentStatuses.Cancelled)); // cancelled never conflicts
        db.TechnicianAppointments.Add(Appointment(other.Id, d, 9));                         // other technician
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Partial_unique_index_allows_one_active_appointment_per_survey_request()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        var tech = await SeedTechnicianAsync(db, "Tech", District1);
        var other = await SeedTechnicianAsync(db, "Other", District1);
        var r = await SeedRequestAsync(db, BinhTan, "R");
        db.TechnicianAppointments.Add(Appointment(tech.Id, r, 8));
        await db.SaveChangesAsync();

        db.TechnicianAppointments.Add(Appointment(other.Id, r, 13));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState);
    }

    [PostgresFact]
    public async Task Two_customers_racing_for_the_last_technician_never_double_book()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest first, second;
        UserAccount tech;
        await using (var db = database.NewContext())
        {
            tech = await SeedTechnicianAsync(db, "Only tech", District1);
            first = await SeedRequestAsync(db, BinhTan, "First");
            second = await SeedRequestAsync(db, BinhTan, "Second");
        }
        HoldUntil(world.Travel, 2);

        await using var dbA = database.NewContext();
        await using var dbB = database.NewContext();
        var results = await Task.WhenAll(
            Task.Run(() => world.Book(dbA).HandleAsync(new BookSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, At(9), null), CancellationToken.None)),
            Task.Run(() => world.Book(dbB).HandleAsync(new BookSurveyAppointmentCommand(second.Customer.Id, second.Request.Id, At(9), null), CancellationToken.None)));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ErrorCode is SchedulingErrorCodes.SlotUnavailable or SchedulingErrorCodes.ConcurrencyConflict);
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.ReservedTechnicianUserId == tech.Id && a.Status != "CANCELLED").ToListAsync());
    }

    [PostgresFact]
    public async Task Concurrent_back_to_back_bookings_are_serialised_and_travel_is_rechecked()
    {
        // 08:00–09:30 in Thu Duc and 09:30–11:00 in Binh Tan do not overlap (the exclusion constraint
        // would allow both), but Thu Duc ↔ Binh Tan takes 60 minutes plus a 30-minute buffer. Each
        // booking alone is feasible; together they are not. The row lock + re-check must reject one.
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        world.Travel.Minutes = r => r.Origin == r.Destination ? 1 : (r.Origin == ThuDuc && r.Destination == BinhTan) || (r.Origin == BinhTan && r.Destination == ThuDuc) ? 60 : 10;
        SeededSurveyRequest morning, late;
        UserAccount tech;
        await using (var db = database.NewContext())
        {
            tech = await SeedTechnicianAsync(db, "Only tech", District1);
            morning = await SeedRequestAsync(db, ThuDuc, "Morning");
            late = await SeedRequestAsync(db, BinhTan, "Late morning");
        }
        HoldUntil(world.Travel, 2);

        await using var dbA = database.NewContext();
        await using var dbB = database.NewContext();
        var results = await Task.WhenAll(
            Task.Run(() => world.Book(dbA).HandleAsync(new BookSurveyAppointmentCommand(morning.Customer.Id, morning.Request.Id, At(8), null), CancellationToken.None)),
            Task.Run(() => world.Book(dbB).HandleAsync(new BookSurveyAppointmentCommand(late.Customer.Id, late.Request.Id, At(9, 30), null), CancellationToken.None)));

        Assert.Single(results, r => r.Succeeded);
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.ReservedTechnicianUserId == tech.Id && a.Status != "CANCELLED").ToListAsync());
    }

    [PostgresFact]
    public async Task Duplicate_concurrent_booking_by_the_same_customer_creates_one_appointment()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest request;
        await using (var db = database.NewContext())
        {
            await SeedTechnicianAsync(db, "Tech A", District1);
            await SeedTechnicianAsync(db, "Tech B", District7);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
        }
        HoldUntil(world.Travel, 2);

        await using var dbA = database.NewContext();
        await using var dbB = database.NewContext();
        var command = new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null);
        var results = await Task.WhenAll(
            Task.Run(() => world.Book(dbA).HandleAsync(command, CancellationToken.None)),
            Task.Run(() => world.Book(dbB).HandleAsync(command, CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
        Assert.Equal(results[0].Value!.AppointmentId, results[1].Value!.AppointmentId);
        await using var check = database.NewContext();
        Assert.Single(await check.TechnicianAppointments.Where(a => a.SurveyRequestId == request.Request.Id).ToListAsync());
    }

    [PostgresFact]
    public async Task Reschedule_racing_with_sales_assignment_leaves_one_consistent_appointment()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest request;
        UserAccount techA, techB, sales;
        await using (var db = database.NewContext())
        {
            techA = await SeedTechnicianAsync(db, "Tech A", District1);
            techB = await SeedTechnicianAsync(db, "Tech B", District7);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
            sales = await SeedUserAsync(db, "Sales", RoleCodes.Sales);
        }
        await using (var db = database.NewContext())
        {
            Assert.True((await world.Book(db).HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
            await new PreSurveyUnitOfWork(db).TryClaimSurveyRequestAsync(request.Request.Id, sales.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        HoldUntil(world.Travel, 2);

        await using var dbCustomer = database.NewContext();
        await using var dbSales = database.NewContext();
        var reschedule = Task.Run(() => world.Reschedule(dbCustomer).HandleAsync(
            new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None));
        var assign = Task.Run(() => world.Assign(dbSales).HandleAsync(
            new AssignTechnicianCommand(sales.Id, request.Request.Id, techB.Id), CancellationToken.None));
        await Task.WhenAll(reschedule, assign);

        Assert.True(reschedule.Result.Succeeded ^ assign.Result.Succeeded,
            $"Exactly one should win: reschedule={reschedule.Result.ErrorCode ?? "ok"}, assign={assign.Result.ErrorCode ?? "ok"}");
        await using var check = database.NewContext();
        var active = await check.TechnicianAppointments.Where(a => a.SurveyRequestId == request.Request.Id && a.Status != "CANCELLED").SingleAsync();
        if (assign.Result.Succeeded)
        {
            Assert.Equal(AppointmentStatuses.Assigned, active.Status);
            Assert.Equal(techB.Id, active.ReservedTechnicianUserId);
            Assert.Equal(At(9).ToUniversalTime(), active.StartsAt);
        }
        else
        {
            Assert.Equal(AppointmentStatuses.Booked, active.Status);
            Assert.Equal(At(14).ToUniversalTime(), active.StartsAt);
        }
        _ = techA;
    }

    [PostgresFact]
    public async Task Reassignment_moves_capacity_atomically_and_frees_the_previous_technician()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var world = new World(database);
        SeededSurveyRequest request, other;
        UserAccount techA, techB, sales;
        await using (var db = database.NewContext())
        {
            techA = await SeedTechnicianAsync(db, "Tech A", District1);
            techB = await SeedTechnicianAsync(db, "Tech B", District7);
            request = await SeedRequestAsync(db, BinhTan, "Customer");
            other = await SeedRequestAsync(db, District1, "Other");
            sales = await SeedUserAsync(db, "Sales", RoleCodes.Sales);
        }
        await using (var db = database.NewContext())
        {
            Assert.True((await world.Book(db).HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
            await new PreSurveyUnitOfWork(db).TryClaimSurveyRequestAsync(request.Request.Id, sales.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        await using (var db = database.NewContext())
        {
            var initial = await db.TechnicianAppointments.AsNoTracking().SingleAsync();
            var target = initial.ReservedTechnicianUserId == techA.Id ? techB : techA;
            var assigned = await world.Assign(db).HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, target.Id), CancellationToken.None);
            Assert.True(assigned.Succeeded, assigned.Message);

            await using var check = database.NewContext();
            var moved = await check.TechnicianAppointments.AsNoTracking().SingleAsync();
            Assert.Equal(target.Id, moved.ReservedTechnicianUserId);
            Assert.Equal(SurveyRequestStatus.Scheduled, (await check.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id)).Status);

            // The previous technician can now hold another 09:00 appointment without violating the exclusion constraint.
            check.TechnicianAppointments.Add(Appointment(initial.ReservedTechnicianUserId, other, 9));
            await check.SaveChangesAsync();
        }
    }
}
