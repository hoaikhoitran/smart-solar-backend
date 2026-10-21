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
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Tests.TestSupport;

public sealed record SeededSurveyRequest(UserAccount Customer, PropertySite Site, Modules.PreSurvey.Entities.PreSurvey PreSurvey, SurveyRequest Request);

/// <summary>
/// Scheduling handlers over the SQLite test database with a fixed clock. Geography uses the REAL official catalog
/// (vn-2025-07-01-hcm-79, seeded from communes.csv): each sample coordinate stands for one real commune of province "79",
/// which belongs to one test operational zone (zone codes are test data); technicians seeded "at" a coordinate are members of
/// that zone, sites seeded there carry the commune's codes.
/// </summary>
public sealed class SchedulingTestContext : IAsyncDisposable
{
    /// <summary>Monday 2026-10-19 06:00 local (UTC+7): every slot of 2026-10-20 is in the future.</summary>
    public static readonly DateTimeOffset DefaultNow = new(2026, 10, 19, 6, 0, 0, TimeSpan.FromHours(7));
    public static readonly DateOnly Day = new(2026, 10, 20);

    // Sample coordinates (Ho Chi Minh City area), each standing for one REAL commune of communes.csv.
    public static readonly GeoPoint District1 = new(10.7769m, 106.7009m);
    public static readonly GeoPoint ThuDuc = new(10.8494m, 106.7537m);
    public static readonly GeoPoint BinhTan = new(10.7653m, 106.6037m);
    public static readonly GeoPoint District7 = new(10.7340m, 106.7220m);

    public const string Province = "79";

    // Real official commune codes (vn-2025-07-01-hcm-79).
    public const string SaiGon = "26740";        // Phường Sài Gòn (former Quận 1)
    public const string BenThanh = "26743";      // Phường Bến Thành (former Quận 1) — never mapped by default
    public const string ThuDucWard = "26824";    // Phường Thủ Đức (former TP Thủ Đức)
    public const string BinhTanWard = "27442";   // Phường Bình Tân (former Quận Bình Tân)
    public const string BinhTriDong = "27448";   // Phường Bình Trị Đông (former Quận Bình Tân) — second commune of the Bình Tân zone
    public const string TanMy = "27487";         // Phường Tân Mỹ (former Quận 7)
    public const string ConDao = "26732";        // Đặc khu Côn Đảo — never mapped by default

    /// <summary>Test zones: code → communes. Each sample coordinate belongs to one of them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> KnownZones = new Dictionary<string, string[]>
    {
        ["Z-D1"] = [SaiGon],
        ["Z-THU-DUC"] = [ThuDucWard],
        ["Z-BINH-TAN"] = [BinhTanWard, BinhTriDong],
        ["Z-D7"] = [TanMy],
    };

    public static string CommuneCodeOf(GeoPoint point)
        => point == District1 ? SaiGon : point == ThuDuc ? ThuDucWard : point == BinhTan ? BinhTanWard : point == District7 ? TanMy : BenThanh;

    public static string ZoneCodeOf(GeoPoint point)
        => point == District1 ? "Z-D1" : point == ThuDuc ? "Z-THU-DUC" : point == BinhTan ? "Z-BINH-TAN" : point == District7 ? "Z-D7" : "Z-OTHER";

    private static string ZoneNameOf(string code) => code switch
    {
        "Z-D1" => "Zone District 1",
        "Z-THU-DUC" => "Zone Thu Duc",
        "Z-BINH-TAN" => "Binh Tan",
        "Z-D7" => "Zone District 7",
        _ => $"Zone {code}",
    };

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

    /// <summary>Bootstrap defaults of onsiteSurvey.* settings (the effective values come from <see cref="Policies"/>).</summary>
    public SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyOptions OnSiteOptions { get; } = new();

    /// <summary>Bootstrap defaults of installation.* settings (the effective values come from <see cref="Policies"/>).</summary>
    public SmartSolar.Modules.Installations.InstallationOptions InstallationOptions { get; } = new();

    /// <summary>The effective settings: bootstrap defaults from the option objects above, overlaid with saved system_setting rows.</summary>
    public SmartSolar.Modules.SystemSettings.ISchedulingPolicyProvider Policies
        => _policies ??= new TestSchedulingPolicyProvider(Db, () => Options, () => OnSiteOptions, () => InstallationOptions, Clock);

    private TestSchedulingPolicyProvider? _policies;

    public WorkingCalendar Calendar { get; private set; }

    /// <summary>Changes scheduling options (e.g. Scheduling:WorkingDays) and rebuilds the calendar; later handlers see the change.</summary>
    public void Reconfigure(Action<SchedulingOptions> configure)
    {
        configure(Options);
        Calendar = new WorkingCalendar(Options);
    }
    public FixedTimeProvider Clock { get; } = new(DefaultNow.ToUniversalTime());

    /// <summary>When true, current installation assignments are busy intervals (Installation Flow enabled).</summary>
    public bool IncludeInstallationAssignments { get; set; }

    public SchedulingUnitOfWork UnitOfWork => new(Db, IncludeInstallationAssignments, Clock);

    public SmartSolar.Infrastructure.Persistence.AdministrativeUnits.AdministrativeCatalogStore Catalog => new(Db);

    public TechnicianEligibility Eligibility(SchedulingUnitOfWork? uow = null) => new(uow ?? UnitOfWork, Calendar, Options, Policies);
    public AppointmentReservationService Reservations(SchedulingUnitOfWork uow) => new(uow, Eligibility(uow), Options, Clock);
    public AppointmentAssignmentService Assignments(SchedulingUnitOfWork uow) => new(uow, Eligibility(uow), Options, Clock, DispatchStore);
    private AppointmentMover Mover(SchedulingUnitOfWork uow) => new(uow, Reservations(uow), Assignments(uow), Options, Clock);

    public GetSurveySlotsHandler Slots() { var u = UnitOfWork; return new(u, Eligibility(u), Coverage(u), Calendar, Options, Clock); }
    public BookSurveyAppointmentHandler Book() { var u = UnitOfWork; return new(u, Reservations(u), Eligibility(u), Coverage(u), Calendar, Clock); }
    public RescheduleSurveyAppointmentHandler Reschedule() { var u = UnitOfWork; return new(u, Mover(u), Eligibility(u), Coverage(u), Calendar, Clock); }
    public CancelSurveyAppointmentHandler Cancel() => new(UnitOfWork, Calendar, Options, Policies, Clock);
    public CustomerSurveyRequestQueries CustomerQueries() => new(UnitOfWork, Calendar, Policies, Clock);
    public TechnicianAvailabilityHandler Availability() { var u = UnitOfWork; return new(u, Eligibility(u), Calendar); }
    public AssignTechnicianHandler Assign() { var u = UnitOfWork; return new(u, Assignments(u), Calendar); }
    public EscalatedAppointmentsHandler Escalated() { var u = UnitOfWork; return new(u, Eligibility(u), Calendar, Clock); }
    public SalesRescheduleHandler SalesReschedule() { var u = UnitOfWork; return new(u, Mover(u), Eligibility(u), Calendar, Clock); }
    public SalesOwnerHandler SalesOwner() => new(UnitOfWork, Clock);
    public TechnicianJobHandlers Jobs() { var u = UnitOfWork; return new(u, Assignments(u), Eligibility(u), Calendar, Clock); }
    public TechnicianScheduleHandler Schedule() => new(UnitOfWork, Calendar, Policies);
    public AdminTechnicianHandlers Admin() { var u = UnitOfWork; return new(u, Assignments(u), Calendar, Options, Clock); }
    public ZoneHandlers Zones() => new(UnitOfWork, Catalog, Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ZoneHandlers>.Instance);
    public ZoneCoverageService Coverage(SmartSolar.Modules.Scheduling.Contracts.Persistence.ISchedulingUnitOfWork? uow = null)
    {
        var u = uow ?? UnitOfWork;
        return new(u, new TechnicianEligibility(u, Calendar, Options, Policies), Clock);
    }
    public AppointmentEscalationService Escalation() => new(UnitOfWork, Clock, Policies);

    // ---- Uncovered-zone dispatch ----
    public DispatchUnitOfWork DispatchStore => new(Db);
    public SmartSolar.Modules.Scheduling.Dispatch.DispatchService Dispatch()
    {
        var u = UnitOfWork;
        return new(u, DispatchStore, Assignments(u), Coverage(u), Calendar, Clock);
    }
    public SmartSolar.Modules.Scheduling.Dispatch.DispatchSweepService DispatchSweep() => new(UnitOfWork, DispatchStore, Clock);

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

    /// <summary>Seeds the official catalog (province 79 + its 168 communes; optionally the legacy map) once.</summary>
    public Task EnsureCatalogAsync(bool withLegacyMap = false) => AdministrativeCatalogTestData.SeedCatalogAsync(Db, withLegacyMap);

    private Guid? _zoneAdminId;

    /// <summary>The ADMIN user recorded as the author of seeded zone mappings.</summary>
    public async Task<Guid> ZoneAdminIdAsync()
    {
        if (_zoneAdminId is { } id) return id;
        var admin = await Inner.SeedUserAsync("Zone admin");
        var role = await RoleAsync(RoleCodes.Admin);
        Db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        _zoneAdminId = admin.Id;
        return admin.Id;
    }

    /// <summary>
    /// The zone with this code, creating it when missing with <paramref name="communeCodes"/> (default: <see cref="KnownZones"/>)
    /// actively mapped. Existing zones are returned unchanged.
    /// </summary>
    public async Task<OperationalZone> EnsureZoneAsync(string code, IReadOnlyList<string>? communeCodes = null, bool active = true, bool serviceable = true)
    {
        var existing = await Db.OperationalZones.AsNoTracking().FirstOrDefaultAsync(z => z.Code == code);
        if (existing is not null) return existing;
        await EnsureCatalogAsync();
        var adminId = await ZoneAdminIdAsync();
        var now = DateTimeOffset.UtcNow;
        var zone = new OperationalZone
        {
            Id = Guid.NewGuid(), Code = code, Name = ZoneNameOf(code), IsActive = active, IsServiceable = serviceable, CreatedAt = now, UpdatedAt = now,
        };
        Db.OperationalZones.Add(zone);
        foreach (var commune in communeCodes ?? (KnownZones.TryGetValue(code, out var known) ? known : []))
            Db.OperationalZoneCommunes.Add(new OperationalZoneCommune
            {
                Id = Guid.NewGuid(), ZoneId = zone.Id, CommuneCode = commune, AssignedByUserId = adminId, AssignedAt = now,
            });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return zone;
    }

    /// <summary>The zone of the coordinate's commune (see <see cref="KnownZones"/>).</summary>
    public Task<OperationalZone> EnsureZoneAsync(GeoPoint point) => EnsureZoneAsync(ZoneCodeOf(point));

    /// <summary>
    /// A user with the TECHNICIAN role and a scheduling profile. With a coordinate, the technician is a member of that
    /// coordinate's zone (null = no zone).
    /// </summary>
    public async Task<UserAccount> SeedTechnicianAsync(string name, GeoPoint? servesZoneOf, bool active = true, bool withRole = true, int? maxPerDay = null)
        => await SeedTechnicianInZonesAsync(name, servesZoneOf is { } point ? [ZoneCodeOf(point)] : [], active, withRole, maxPerDay);

    /// <summary>A technician who is a member of several zones (created from <see cref="KnownZones"/> when missing).</summary>
    public async Task<UserAccount> SeedTechnicianInZonesAsync(string name, IReadOnlyList<string> zoneCodes, bool active = true, bool withRole = true, int? maxPerDay = null)
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
            IsSchedulingActive = active,
            MaxAppointmentsPerDay = maxPerDay,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var zones = new List<OperationalZone>();
        foreach (var code in zoneCodes) zones.Add(await EnsureZoneAsync(code));   // (may clear the change tracker)
        foreach (var zone in zones)
            Db.TechnicianZoneMemberships.Add(new TechnicianZoneMembership
            {
                Id = Guid.NewGuid(), TechnicianUserId = user.Id, ZoneId = zone.Id, AssignedAt = DateTimeOffset.UtcNow,
            });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    /// <summary>
    /// Customer → site at <paramref name="location"/> → submitted pre-survey → pending survey request. The site carries province
    /// "79" and the location's real commune code (or <paramref name="communeCode"/>) unless <paramref name="withCodes"/> is false.
    /// </summary>
    public async Task<SeededSurveyRequest> SeedSurveyRequestAsync(GeoPoint? location, string customerName = "Customer", bool withCodes = true, string? communeCode = null)
    {
        var user = await Inner.SeedUserAsync(customerName);
        var customer = await Inner.SeedCustomerAsync(user.Id);
        var site = PreSurveyTestContext.NewPropertySite(customer.Id);
        site.Latitude = location?.Latitude;
        site.Longitude = location?.Longitude;
        if (withCodes && (location is not null || communeCode is not null))
        {
            await EnsureCatalogAsync();
            site.ProvinceCode = Province;
            site.CommuneCode = communeCode ?? CommuneCodeOf(location!.Value);
        }
        Db.Add(site);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        var preSurvey = await Inner.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);
        var request = await Inner.SeedSurveyRequestAsync(preSurvey.Id);
        return new SeededSurveyRequest(user, site, preSurvey, request);
    }

    /// <summary>A user holding <paramref name="roleCode"/> (Manager/Admin dispatch recipients and actors).</summary>
    public async Task<UserAccount> SeedStaffAsync(string name, string roleCode)
    {
        var user = await Inner.SeedUserAsync(name);
        var role = await RoleAsync(roleCode);
        Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    /// <summary>An Admin update of the system settings through the real service (version-checked, audited); asserts success.</summary>
    public async Task<int> UpdateSettingsAsync(Guid adminUserId, params (string Key, int Value)[] values)
    {
        var uow = new SmartSolar.Infrastructure.Persistence.SystemSettings.SystemSettingsUnitOfWork(Db);
        var definitions = SmartSolar.Modules.SystemSettings.SchedulingPolicyDefinitions.Create(Options, OnSiteOptions, InstallationOptions);
        var cache = new SmartSolar.Modules.SystemSettings.SchedulingPolicyCache(TimeSpan.Zero);
        var service = new SmartSolar.Modules.SystemSettings.SystemSettingsService(uow, definitions, Policies, cache, Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmartSolar.Modules.SystemSettings.SystemSettingsService>.Instance);
        var current = (await Policies.GetAsync(CancellationToken.None)).Version;
        var result = await service.UpdateSchedulingAsync(adminUserId, new SmartSolar.Modules.SystemSettings.SchedulingSettingsUpdateInput(
            values.Select(v => new SmartSolar.Modules.SystemSettings.SystemSettingChangeInput(v.Key, v.Value, null)).ToList(), current, "test"), CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
        Db.ChangeTracker.Clear();
        return result.Value!.Version;
    }

    public async Task<UserAccount> SeedSalesAsync(string name = "Sales User")
    {
        var sales = await Inner.SeedSalesUserAsync(name);
        var role = await RoleAsync(RoleCodes.Sales);
        Db.UserRoles.Add(new UserRole { UserId = sales.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return sales;
    }

    public async Task<UserAccount> ClaimAsync(SeededSurveyRequest seeded)
    {
        var sales = await SeedSalesAsync();
        Assert.True(await Inner.UnitOfWork.TryClaimSurveyRequestAsync(seeded.Request.Id, sales.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        Db.ChangeTracker.Clear();
        return sales;
    }

    /// <summary>Books <paramref name="request"/> at <paramref name="start"/> and asserts success.</summary>
    public async Task<TechnicianAppointment> BookAsync(SeededSurveyRequest request, DateTimeOffset start)
    {
        var booked = await Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, start, null), CancellationToken.None);
        Assert.True(booked.Succeeded, booked.Message);
        return await ActiveAppointmentAsync(request.Request.Id);
    }

    public Task<SchedulingResult<AssignmentView>> AcceptAsync(Guid technicianUserId, Guid appointmentId, int? expectedRevision = null)
        => Jobs().AcceptAsync(new AcceptAppointmentCommand(technicianUserId, appointmentId, expectedRevision), CancellationToken.None);

    public async Task<TechnicianAppointment> ActiveAppointmentAsync(Guid surveyRequestId)
        => await Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.SurveyRequestId == surveyRequestId && a.Status != "CANCELLED");

    public async Task<TechnicianAppointment> AppointmentAsync(Guid appointmentId)
        => await Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId);

    public async Task<SurveyRequest> RequestAsync(Guid surveyRequestId)
        => await Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == surveyRequestId);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
