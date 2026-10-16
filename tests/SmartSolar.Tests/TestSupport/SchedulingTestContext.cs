using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Tests.TestSupport;

public sealed record SeededSurveyRequest(UserAccount Customer, PropertySite Site, Modules.PreSurvey.Entities.PreSurvey PreSurvey, SurveyRequest Request);

/// <summary>Scheduling handlers over the SQLite test database with a fixed clock and synthetic travel times.</summary>
public sealed class SchedulingTestContext : IAsyncDisposable
{
    /// <summary>Monday 2026-10-19 06:00 local (UTC+7): every slot of 2026-10-20 is in the future.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 10, 19, 6, 0, 0, TimeSpan.FromHours(7));
    public static readonly DateOnly Day = new(2026, 10, 20);

    // SYNTHETIC coordinates (Ho Chi Minh City area).
    public static readonly GeoPoint District1 = new(10.7769m, 106.7009m);
    public static readonly GeoPoint ThuDuc = new(10.8494m, 106.7537m);
    public static readonly GeoPoint BinhTan = new(10.7653m, 106.6037m);
    public static readonly GeoPoint District7 = new(10.7340m, 106.7220m);

    private SchedulingTestContext(PreSurveyTestContext inner)
    {
        Inner = inner;
        Calendar = new WorkingCalendar(Options);
    }

    public static SchedulingTestContext Create() => new(new PreSurveyTestContext());

    public static SchedulingTestContext CreateFileBacked() => new(PreSurveyTestContext.CreateFileBacked());

    public static SchedulingTestContext CreateOn(AppDbContext db) => new(PreSurveyTestContext.ForExisting(db));

    public PreSurveyTestContext Inner { get; }
    public AppDbContext Db => Inner.Db;
    public SchedulingOptions Options { get; } = new();
    public WorkingCalendar Calendar { get; }
    public FixedTimeProvider Clock { get; } = new(DefaultNow.ToUniversalTime());
    public FakeTravelTimeEstimator Travel { get; } = new();

    public SchedulingUnitOfWork UnitOfWork => new(Db);

    private TechnicianAvailabilityEvaluator Evaluator => new(Options, Clock);
    private AppointmentReservationService Reservations(SchedulingUnitOfWork uow) => new(uow, Evaluator, Travel, Options, Clock, Calendar);

    /// <summary>The durable recalculation service (synthetic travel; no provider is called).</summary>
    public ScheduleRecalculationService Recalculation(SchedulingUnitOfWork? uow = null)
        => new(uow ?? UnitOfWork, Travel, Calendar, Options, Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduleRecalculationService>.Instance);

    public GetSurveySlotsHandler Slots() => new(UnitOfWork, Evaluator, Travel, Calendar, Options, Clock);
    public BookSurveyAppointmentHandler Book() { var u = UnitOfWork; return new(u, Reservations(u), Calendar, Clock); }
    public RescheduleSurveyAppointmentHandler Reschedule() { var u = UnitOfWork; return new(u, Reservations(u), Recalculation(u), Calendar, Clock); }
    public CancelSurveyAppointmentHandler Cancel() { var u = UnitOfWork; return new(u, Recalculation(u), Calendar, Clock); }
    public CustomerSurveyRequestQueries CustomerQueries() => new(UnitOfWork, Calendar, Clock);
    public TechnicianAvailabilityHandler Availability() => new(UnitOfWork, Evaluator, Travel, Calendar, Options, Clock);
    public AssignTechnicianHandler Assign() { var u = UnitOfWork; return new(u, Reservations(u), Recalculation(u), new TechnicianDayLoader(u, Calendar), Calendar, Options, Clock); }
    public SalesRescheduleHandler SalesReschedule() { var u = UnitOfWork; return new(u, Reservations(u), Recalculation(u), Calendar, Clock); }
    public TechnicianJobHandlers Jobs() => new(UnitOfWork, Calendar, Clock);
    public TechnicianScheduleHandler Schedule() => new(UnitOfWork, Calendar, Options, Clock);
    public AdminTechnicianHandlers Admin() { var u = UnitOfWork; return new(u, Recalculation(u), Calendar, Clock); }

    /// <summary>Local wall-clock time on <see cref="Day"/> (or another date) as an offset timestamp.</summary>
    public static DateTimeOffset At(int hour, int minute = 0, DateOnly? date = null)
    {
        var d = date ?? Day;
        return new DateTimeOffset(d.Year, d.Month, d.Day, hour, minute, 0, TimeSpan.FromHours(7));
    }

    private async Task<Role> RoleAsync(string code)
    {
        var role = await Db.Roles.FirstOrDefaultAsync(r => r.Code == code);
        if (role is not null) return role;
        role = new Role { Id = Guid.NewGuid(), Code = code, Name = code };
        Db.Roles.Add(role);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return role;
    }

    /// <summary>A user with the TECHNICIAN role in the database and (optionally) an active profile at <paramref name="baseLocation"/>.</summary>
    public async Task<UserAccount> SeedTechnicianAsync(string name, GeoPoint? baseLocation, bool active = true, bool withRole = true, int? maxPerDay = null)
    {
        var user = await Inner.SeedUserAsync(name);
        if (withRole)
        {
            var role = await RoleAsync(RoleCodes.Technician);
            Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        }
        Db.TechnicianProfiles.Add(new TechnicianProfile
        {
            UserId = user.Id,
            BaseLatitude = baseLocation?.Latitude,
            BaseLongitude = baseLocation?.Longitude,
            IsSchedulingActive = active,
            MaxAppointmentsPerDay = maxPerDay,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    /// <summary>Customer → site at <paramref name="location"/> → submitted pre-survey → pending survey request.</summary>
    public async Task<SeededSurveyRequest> SeedSurveyRequestAsync(GeoPoint? location, string customerName = "Customer")
    {
        var user = await Inner.SeedUserAsync(customerName);
        var customer = await Inner.SeedCustomerAsync(user.Id);
        var site = PreSurveyTestContext.NewPropertySite(customer.Id);
        site.Latitude = location?.Latitude;
        site.Longitude = location?.Longitude;
        Db.Add(site);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var preSurvey = await Inner.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);
        var request = await Inner.SeedSurveyRequestAsync(preSurvey.Id);
        return new SeededSurveyRequest(user, site, preSurvey, request);
    }

    public async Task<UserAccount> ClaimAsync(SeededSurveyRequest seeded)
    {
        var sales = await Inner.SeedSalesUserAsync();
        Assert.True(await Inner.UnitOfWork.TryClaimSurveyRequestAsync(seeded.Request.Id, sales.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        Db.ChangeTracker.Clear();
        return sales;
    }

    public async Task<TechnicianAppointment> ActiveAppointmentAsync(Guid surveyRequestId)
        => await Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.SurveyRequestId == surveyRequestId && a.Status != "CANCELLED");

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
