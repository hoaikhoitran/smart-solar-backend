using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.IotMonitoring;

/// <summary>
/// Binding rules: one ACTIVE binding per device and per customer, only enabled devices and non-cancelled projects, a release keeps the
/// history, and readings stay with the binding they arrived under when a device moves to another customer.
/// </summary>
public sealed class IotBindingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_manager_binds_a_device_to_the_customers_project_and_only_that_customer_sees_it()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var b = await ctx.BoundAsync(admin, manager);

        var binding = await ctx.Db.IotDeviceBindings.AsNoTracking().SingleAsync();
        Assert.Equal((IotBindingStatuses.Active, b.Project, b.Customer, manager, (DateTimeOffset?)null), (binding.Status, binding.ProjectId, binding.CustomerUserId, binding.BoundByUserId, binding.ReleasedAt));
        Assert.Equal(b.BindingId, (await ctx.Devices().GetAsync(b.Device, None)).Value!.CurrentBinding!.BindingId);

        var view = await ctx.Telemetry().GetProjectDeviceAsync(b.Customer, false, b.Project, None);
        Assert.True(view.Succeeded, view.Message);
        Assert.Equal((b.Code, IotConnectionStatuses.NoData, (DateTimeOffset?)null), (view.Value!.DeviceId, view.Value.ConnectionStatus, view.Value.LastReadingAt));
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await ctx.Telemetry().GetProjectDeviceAsync(Guid.NewGuid(), false, b.Project, None)).ErrorCode);
        Assert.True((await ctx.Telemetry().GetProjectDeviceAsync(manager, true, b.Project, None)).Succeeded);

        // Binding the same device to the same project again is idempotent.
        var again = await ctx.Bindings().BindAsync(manager, b.Project, b.Code.ToLowerInvariant(), null, false, None);
        Assert.Equal(b.BindingId, again.Value!.BindingId);
        Assert.Equal(1, await ctx.Db.IotDeviceBindings.CountAsync());
    }

    [Fact]
    public async Task A_customer_with_three_projects_has_one_device_per_project_and_reads_each_project_separately()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var customer = await ctx.UserAsync("Customer A");
        var p1 = await ctx.ProjectAsync(customer);
        var p2 = await ctx.ProjectAsync(customer);
        var p3 = await ctx.ProjectAsync(customer);
        var d1 = await ctx.DeviceAsync(admin, "ESP32-P1");
        var d2 = await ctx.DeviceAsync(admin, "ESP32-P2");
        var bindings = ctx.Bindings();
        var telemetry = ctx.Telemetry();

        Assert.True((await bindings.BindAsync(manager, p1, d1.Code, null, false, None)).Succeeded);
        Assert.True((await bindings.BindAsync(manager, p2, d2.Code, null, false, None)).Succeeded);
        Assert.Equal(2, await ctx.Db.IotDeviceBindings.CountAsync(b => b.CustomerUserId == customer && b.Status == IotBindingStatuses.Active));

        Assert.True((await telemetry.IngestAsync(d1.Code, d1.Secret, 1.1, None)).Succeeded);
        Assert.True((await telemetry.IngestAsync(d2.Code, d2.Secret, 2.2, None)).Succeeded);

        // Every read is scoped by projectId: each project shows only its own device and readings.
        Assert.Equal((d1.Code, 1.1), ((await telemetry.GetProjectDeviceAsync(customer, false, p1, None)).Value!.DeviceId,
            (await telemetry.GetLatestAsync(customer, false, p1, None)).Value!.Reading!.CurrentAmps));
        Assert.Equal((d2.Code, 2.2), ((await telemetry.GetProjectDeviceAsync(customer, false, p2, None)).Value!.DeviceId,
            (await telemetry.GetLatestAsync(customer, false, p2, None)).Value!.Reading!.CurrentAmps));
        Assert.Equal([1.1], (await telemetry.GetHistoryAsync(customer, false, p1, null, null, null, null, None)).Value!.Items.Select(r => r.CurrentAmps));
        Assert.Equal([2.2], (await telemetry.GetHistoryAsync(customer, false, p2, null, null, null, null, None)).Value!.Items.Select(r => r.CurrentAmps));
        Assert.Equal(1.1, (await ctx.Monitoring().GetAsync(customer, false, p1, None)).Value!.Measured!.Latest!.CurrentAmps);
        Assert.Equal(2.2, (await ctx.Monitoring().GetAsync(customer, false, p2, None)).Value!.Measured!.Latest!.CurrentAmps);

        // Project 3 has no device yet.
        Assert.Equal(IotErrorCodes.BindingNotFound, (await telemetry.GetLatestAsync(customer, false, p3, None)).ErrorCode);
        Assert.Empty((await telemetry.GetHistoryAsync(customer, false, p3, null, null, null, null, None)).Value!.Items);
        Assert.Null((await ctx.Monitoring().GetAsync(customer, false, p3, None)).Value!.Measured);

        // Another customer sees none of these projects.
        var other = await ctx.UserAsync("Customer B");
        foreach (var project in new[] { p1, p2, p3 })
        {
            Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await telemetry.GetLatestAsync(other, false, project, None)).ErrorCode);
            Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await telemetry.GetHistoryAsync(other, false, project, null, null, null, null, None)).ErrorCode);
            Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await ctx.Monitoring().GetAsync(other, false, project, None)).ErrorCode);
        }
    }

    [Fact]
    public async Task One_device_per_project_and_one_project_per_device()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var customer = await ctx.UserAsync("Customer");
        var p1 = await ctx.ProjectAsync(customer);
        var p2 = await ctx.ProjectAsync(customer);
        var d1 = await ctx.DeviceAsync(admin, "ESP32-A");
        var d2 = await ctx.DeviceAsync(admin, "ESP32-B");
        var bindings = ctx.Bindings();
        Assert.True((await bindings.BindAsync(manager, p1, d1.Code, null, false, None)).Succeeded);

        var secondOnP1 = await bindings.BindAsync(manager, p1, d2.Code, null, false, None);
        Assert.Equal(IotErrorCodes.ProjectAlreadyBound, secondOnP1.ErrorCode);
        var d1OnP2 = await bindings.BindAsync(manager, p2, d1.Code, null, false, None);
        Assert.Equal(IotErrorCodes.DeviceAlreadyBound, d1OnP2.ErrorCode);
        // replaceCurrent never steals a device that is bound elsewhere.
        Assert.Equal(IotErrorCodes.DeviceAlreadyBound, (await bindings.BindAsync(manager, p2, d1.Code, null, true, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.IotDeviceBindings.CountAsync());
    }

    [Fact]
    public async Task Replacing_the_device_of_one_project_leaves_the_customers_other_projects_untouched()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var customer = await ctx.UserAsync("Customer");
        var p1 = await ctx.ProjectAsync(customer);
        var p2 = await ctx.ProjectAsync(customer);
        var d1 = await ctx.DeviceAsync(admin, "ESP32-1");
        var d2 = await ctx.DeviceAsync(admin, "ESP32-2");
        var d3 = await ctx.DeviceAsync(admin, "ESP32-3");
        var bindings = ctx.Bindings();
        var b1 = (await bindings.BindAsync(manager, p1, d1.Code, null, false, None)).Value!;
        var b2 = (await bindings.BindAsync(manager, p2, d2.Code, null, false, None)).Value!;
        Assert.True((await ctx.Telemetry().IngestAsync(d1.Code, d1.Secret, 0.5, None)).Succeeded);

        var replaced = await bindings.BindAsync(manager, p1, d3.Code, "swap", true, None);
        Assert.True(replaced.Succeeded, replaced.Message);

        var old = await ctx.Db.IotDeviceBindings.AsNoTracking().SingleAsync(x => x.Id == b1.BindingId);
        Assert.Equal((IotBindingStatuses.Released, manager, $"Replaced by device {d3.Code}"), (old.Status, old.ReleasedByUserId, old.ReleaseReason));
        Assert.Equal(IotBindingStatuses.Active, (await ctx.Db.IotDeviceBindings.AsNoTracking().SingleAsync(x => x.Id == b2.BindingId)).Status);
        Assert.Equal(d2.Code, (await ctx.Telemetry().GetProjectDeviceAsync(customer, false, p2, None)).Value!.DeviceId);
        Assert.Equal(d3.Code, (await ctx.Telemetry().GetProjectDeviceAsync(customer, false, p1, None)).Value!.DeviceId);
        // Project 1 keeps the reading of its former device in its history.
        Assert.Equal([0.5], (await ctx.Telemetry().GetHistoryAsync(customer, false, p1, null, null, null, null, None)).Value!.Items.Select(r => r.CurrentAmps));
        // The freed device can go to another project.
        Assert.True((await bindings.BindAsync(manager, await ctx.ProjectAsync(customer), d1.Code, null, false, None)).Succeeded);
    }

    [Fact]
    public async Task Binding_refuses_disabled_devices_cancelled_projects_and_unknown_ids()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var customer = await ctx.UserAsync("Customer");
        var project = await ctx.ProjectAsync(customer);
        var cancelled = await ctx.ProjectAsync(customer, InstallationStatuses.Cancelled);
        var (deviceId, code, _) = await ctx.DeviceAsync(admin);
        var bindings = ctx.Bindings();

        Assert.Equal(IotErrorCodes.ValidationFailed, (await bindings.BindAsync(manager, project, "x", null, false, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceNotFound, (await bindings.BindAsync(manager, project, "ESP32-404", null, false, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await bindings.BindAsync(manager, Guid.NewGuid(), code, null, false, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.ProjectNotBindable, (await bindings.BindAsync(manager, cancelled, code, null, false, None)).ErrorCode);
        Assert.True((await ctx.Devices().DisableAsync(admin, deviceId, "Faulty", null, None)).Succeeded);
        Assert.Equal(IotErrorCodes.DeviceDisabled, (await bindings.BindAsync(manager, project, code, null, false, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.IotDeviceBindings.CountAsync());
        Assert.Equal(IotErrorCodes.BindingNotFound, (await bindings.ReleaseAsync(manager, Guid.NewGuid(), "x", None)).ErrorCode);
    }

    [Fact]
    public async Task Release_keeps_history_and_a_moved_device_never_shows_the_previous_customers_readings()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var a = await ctx.BoundAsync(admin, manager, "ESP32-001", "Customer A");
        var telemetry = ctx.Telemetry();
        Assert.True((await telemetry.IngestAsync(a.Code, a.Secret, 0.21, None)).Succeeded);
        ctx.Clock.Now += TimeSpan.FromSeconds(10);
        Assert.True((await telemetry.IngestAsync(a.Code, a.Secret, 0.22, None)).Succeeded);

        var bindings = ctx.Bindings();
        Assert.Equal(IotErrorCodes.ValidationFailed, (await bindings.ReleaseAsync(manager, a.BindingId, "", None)).ErrorCode);
        var released = await bindings.ReleaseAsync(manager, a.BindingId, "Customer moved out", None);
        Assert.True(released.Succeeded, released.Message);
        Assert.Equal((IotBindingStatuses.Released, "Customer moved out"), (released.Value!.Status, released.Value.ReleaseReason));
        Assert.Equal(IotErrorCodes.BindingNotActive, (await bindings.ReleaseAsync(manager, a.BindingId, "again", None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceNotBound, (await telemetry.IngestAsync(a.Code, a.Secret, 0.23, None)).ErrorCode);
        Assert.Equal(IotConnectionStatuses.Unassigned, (await ctx.Devices().GetAsync(a.Device, None)).Value!.ConnectionStatus);

        var customerB = await ctx.UserAsync("Customer B");
        var projectB = await ctx.ProjectAsync(customerB);
        Assert.True((await bindings.BindAsync(manager, projectB, a.Code, null, false, None)).Succeeded);
        ctx.Clock.Now += TimeSpan.FromSeconds(10);
        Assert.True((await telemetry.IngestAsync(a.Code, a.Secret, 0.5, None)).Succeeded);

        var historyB = (await telemetry.GetHistoryAsync(customerB, false, projectB, null, null, null, null, None)).Value!;
        Assert.Equal([0.5], historyB.Items.Select(r => r.CurrentAmps));
        var historyA = (await telemetry.GetHistoryAsync(a.Customer, false, a.Project, null, null, null, null, None)).Value!;
        Assert.Equal([0.22, 0.21], historyA.Items.Select(r => r.CurrentAmps));
        Assert.Equal(IotErrorCodes.BindingNotFound, (await telemetry.GetLatestAsync(a.Customer, false, a.Project, None)).ErrorCode);
        Assert.Equal(0.5, (await telemetry.GetLatestAsync(customerB, false, projectB, None)).Value!.Reading!.CurrentAmps);

        var all = (await bindings.ListAsync(null, a.Code, null, None)).Value!;
        Assert.Equal([IotBindingStatuses.Active, IotBindingStatuses.Released], all.Select(b => b.Status));
        Assert.Single((await bindings.ListAsync(a.Project, null, IotBindingStatuses.Released, None)).Value!);
        Assert.Equal(3, await ctx.Db.IotTelemetryReadings.CountAsync());
        Assert.Equal(ctx.Clock.GetUtcNow(), (await ctx.Db.IotDevices.AsNoTracking().SingleAsync()).LastSeenAt);
    }
}
