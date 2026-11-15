using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Infrastructure.Persistence.IotMonitoring;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// IoT handlers wired exactly like <c>AddIotMonitoring</c> over the Installation Flow test context (SQLite, fixed clock). A seeded
/// installation project is a plain row (foreign keys off for the insert): the IoT rules only need the project id, its customer and status.
/// </summary>
public sealed class IotTestContext : IAsyncDisposable
{
    private readonly bool _ownsInstallation;

    private IotTestContext(InstallationWorkflowTestContext installation, bool ownsInstallation)
    {
        Installation = installation;
        _ownsInstallation = ownsInstallation;
    }

    public static IotTestContext Create() => new(InstallationWorkflowTestContext.Create(), true);

    /// <summary>Over an existing context, e.g. a disposable PostgreSQL database.</summary>
    public static IotTestContext CreateOn(AppDbContext db) => new(InstallationWorkflowTestContext.CreateOn(db), true);

    /// <summary>IoT handlers over a journey's own context (the caller disposes that context).</summary>
    public static IotTestContext Over(InstallationWorkflowTestContext installation) => new(installation, false);

    public InstallationWorkflowTestContext Installation { get; }
    public AppDbContext Db => Installation.Db;
    public FixedTimeProvider Clock => Installation.Clock;
    public IotMonitoringOptions Options { get; } = new();

    public IotDeviceHandlers Devices(AppDbContext? db = null) => new(new IotMonitoringUnitOfWork(db ?? Db), Options, Clock);
    public IotBindingHandlers Bindings(AppDbContext? db = null) => new(new IotMonitoringUnitOfWork(db ?? Db), Clock);
    public IotTelemetryHandlers Telemetry(AppDbContext? db = null) => new(new IotMonitoringUnitOfWork(db ?? Db), Options, Clock, NullLogger<IotTelemetryHandlers>.Instance);

    /// <summary>The production wiring of the monitoring view: simulated estimate plus the measured block of the bound device.</summary>
    public EnergyMonitoringHandlers Monitoring(AppDbContext? db = null) => new(new InstallationUnitOfWork(db ?? Db), Telemetry(db));

    public Task<Guid> AdminAsync(string name = "IoT admin") => UserAsync(name);
    public Task<Guid> ManagerAsync(string name = "IoT manager") => UserAsync(name);

    public async Task<Guid> UserAsync(string name) => (await Installation.Scheduling.Inner.SeedUserAsync(name)).Id;

    /// <summary>A registered, enabled device; returns its row id, firmware id and raw secret.</summary>
    public async Task<(Guid Id, string Code, string Secret)> DeviceAsync(Guid admin, string code = "ESP32-001", decimal? maxCurrentAmps = null)
    {
        var registered = await Devices().RegisterAsync(admin, new DeviceRegistrationInput(code, $"Device {code}", "ACS712-30A", maxCurrentAmps), CancellationToken.None);
        Assert.True(registered.Succeeded, registered.Message);
        return (registered.Value!.Device.Id, registered.Value.Device.DeviceId, registered.Value.Secret);
    }

    /// <summary>An installation project row of <paramref name="customerUserId"/> in the given status (no contract, no survey: not needed by IoT).</summary>
    public async Task<Guid> ProjectAsync(Guid customerUserId, string status = InstallationStatuses.Completed)
    {
        var now = Clock.GetUtcNow();
        var id = Guid.NewGuid();
        var sqlite = Db.Database.IsSqlite();
        if (!sqlite) throw new InvalidOperationException("Seed a real journey (e.g. AfterSalesTestContext.HandedOverAsync) on PostgreSQL: its foreign keys are enforced.");
        await Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try
        {
            var completed = status == InstallationStatuses.Completed;
            Db.InstallationProjects.Add(new InstallationProject
            {
                Id = id, ContractId = Guid.NewGuid(), SurveyRequestId = Guid.NewGuid(), CustomerUserId = customerUserId, PropertySiteId = Guid.NewGuid(),
                ManagerUserId = customerUserId, Status = status, StartedAt = completed ? now.AddDays(-3) : null, CompletedAt = completed ? now.AddDays(-1) : null,
                HandoverAt = completed ? now.AddDays(-1) : null, AcceptanceRound = completed ? 1 : 0, CancelledAt = status == InstallationStatuses.Cancelled ? now : null,
                CancellationReason = status == InstallationStatuses.Cancelled ? "test" : null, CreatedAt = now.AddDays(-5), UpdatedAt = now,
            });
            await Db.SaveChangesAsync();
        }
        finally
        {
            await Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
            Db.ChangeTracker.Clear();
        }
        return id;
    }

    /// <summary>A customer with one project, and a device bound to it by <paramref name="manager"/>.</summary>
    public async Task<(Guid Customer, Guid Project, Guid Device, string Code, string Secret, Guid BindingId)> BoundAsync(Guid admin, Guid manager, string code = "ESP32-001",
        string customerName = "IoT customer")
    {
        var customer = await UserAsync(customerName);
        var project = await ProjectAsync(customer);
        var device = await DeviceAsync(admin, code);
        var bound = await Bindings().BindAsync(manager, project, code, null, false, CancellationToken.None);
        Assert.True(bound.Succeeded, bound.Message);
        return (customer, project, device.Id, device.Code, device.Secret, bound.Value!.BindingId);
    }

    public ValueTask DisposeAsync() => _ownsInstallation ? Installation.DisposeAsync() : ValueTask.CompletedTask;
}
