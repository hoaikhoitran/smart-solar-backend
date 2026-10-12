using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Entities;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<AuthActionToken> AuthActionTokens => Set<AuthActionToken>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<PropertySite> PropertySites => Set<PropertySite>();

    public DbSet<PreSurvey> PreSurveys => Set<PreSurvey>();

    public DbSet<Product> Products => Set<Product>();
    public DbSet<SurveyRequest> SurveyRequests
    => Set<SurveyRequest>();

    public DbSet<SimulationEntity> SolarSimulations => Set<SimulationEntity>();

    public DbSet<TechnicianProfile> TechnicianProfiles => Set<TechnicianProfile>();

    public DbSet<TechnicianUnavailablePeriod> TechnicianUnavailablePeriods => Set<TechnicianUnavailablePeriod>();

    public DbSet<TechnicianAppointment> TechnicianAppointments => Set<TechnicianAppointment>();

    public DbSet<TechnicianTravelEstimate> TechnicianTravelEstimates => Set<TechnicianTravelEstimate>();

    public DbSet<TechnicianWorkingPeriod> TechnicianWorkingPeriods => Set<TechnicianWorkingPeriod>();

    public DbSet<TechnicianScheduleRecalculation> TechnicianScheduleRecalculations => Set<TechnicianScheduleRecalculation>();

    public DbSet<TechnicianAppointmentEvent> TechnicianAppointmentEvents => Set<TechnicianAppointmentEvent>();

    public DbSet<SchedulingNotification> SchedulingNotifications => Set<SchedulingNotification>();

    public DbSet<UserAccountAuditEntry> UserAccountAuditEntries => Set<UserAccountAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AppDbContext).Assembly);
    }
}
