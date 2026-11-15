using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// E2E-IoT: a real handed-over installation (survey → quotation → contract → payment → installation → acceptance), then the device
/// life cycle on the same database: Admin registers → Manager binds → the ESP32 sends readings every 10 s → the customer reads the
/// measured data next to the unchanged simulated estimate → the device moves to another customer → it is disabled. Handlers only; the
/// HTTP layer is covered by <c>IotEndpointsTests</c>.
/// </summary>
public sealed class IotEnergyMonitoringE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Provision_bind_ingest_read_move_and_disable_on_a_handed_over_installation()
    {
        await using var ctx = AfterSalesTestContext.Create();
        var h = await ctx.HandedOverAsync();
        await using var iot = IotTestContext.Over(ctx.Installation);
        var clock = ctx.Installation.Clock;

        // Before any device: the monitoring view is the simulated estimate only, also through the legacy single-dependency wiring.
        var legacy = await new EnergyMonitoringHandlers(new InstallationUnitOfWork(ctx.Installation.Db)).GetAsync(h.CustomerId, false, h.ProjectId, None);
        Assert.True(legacy.Succeeded, legacy.Message);
        var baseline = legacy.Value!;
        Assert.Equal((EnergyMonitoringValues.Simulated, false, (string?)null, (IotMeasuredMonitoringView?)null), (baseline.DataSource, baseline.MeasuredDataAvailable, baseline.MeasuredDataSource, baseline.Measured));
        Assert.Equal(baseline, (await iot.Monitoring().GetAsync(h.CustomerId, false, h.ProjectId, None)).Value);

        // Admin provisions, Manager binds.
        var credential = (await iot.Devices().RegisterAsync(h.AdminId, new DeviceRegistrationInput("ESP32-E2E", "Inverter current clamp", "ACS712-30A", null), None)).Value!;
        var binding = (await iot.Bindings().BindAsync(h.ManagerId, h.ProjectId, "esp32-e2e", "Clamp on the AC output", false, None)).Value!;
        Assert.Equal((h.CustomerId, IotBindingStatuses.Active), (binding.CustomerUserId, binding.Status));
        Assert.Equal(IotConnectionStatuses.NoData, (await iot.Telemetry().GetProjectDeviceAsync(h.CustomerId, false, h.ProjectId, None)).Value!.ConnectionStatus);

        // The firmware cycle: one reading every 10 s.
        var telemetry = iot.Telemetry();
        var values = new[] { 0.2145, 0.3012, -0.1000 };
        foreach (var value in values)
        {
            clock.Now += TimeSpan.FromSeconds(10);
            var sent = await telemetry.IngestAsync(credential.Device.DeviceId, credential.Secret, value, None);
            Assert.True(sent.Succeeded, sent.Message);
            Assert.Equal(clock.GetUtcNow(), sent.Value!.ReceivedAt);
        }

        // Customer: measured next to simulated, clearly labelled; staff see the same.
        var view = (await iot.Monitoring().GetAsync(h.CustomerId, false, h.ProjectId, None)).Value!;
        Assert.Equal(baseline.Simulated, view.Simulated);
        Assert.Equal((EnergyMonitoringValues.Simulated, true, EnergyMonitoringValues.MeasuredDataAvailable, IotViewMapping.MeasuredSource),
            (view.DataSource, view.MeasuredDataAvailable, view.Status, view.MeasuredDataSource));
        Assert.Equal((-0.1, IotMeasurementStatuses.Measured, IotMeasurementStatuses.NotAvailable, IotConnectionStatuses.Online),
            (view.Measured!.Latest!.CurrentAmps, view.Measured.CurrentAmps.Status, view.Measured.PowerWatts.Status, view.Measured.Device.ConnectionStatus));
        var history = (await telemetry.GetHistoryAsync(h.CustomerId, false, h.ProjectId, null, null, null, null, None)).Value!;
        Assert.Equal(values.Reverse(), history.Items.Select(r => r.CurrentAmps));
        Assert.Equal(history.Items, (await telemetry.GetHistoryAsync(h.ManagerId, true, h.ProjectId, null, null, null, null, None)).Value!.Items);

        // Silence beyond the timeout: OFFLINE, last value still shown as the latest.
        clock.Now += TimeSpan.FromSeconds(iot.Options.OfflineAfterSeconds + 1);
        var offline = (await telemetry.GetLatestAsync(h.CustomerId, false, h.ProjectId, None)).Value!;
        Assert.Equal((IotConnectionStatuses.Offline, -0.1), (offline.ConnectionStatus, offline.Reading!.CurrentAmps));

        // The device moves to another customer: the first customer keeps their history, the second starts empty.
        var otherCustomer = await iot.UserAsync("Second customer");
        var otherProject = await iot.ProjectAsync(otherCustomer);
        Assert.True((await iot.Bindings().ReleaseAsync(h.ManagerId, binding.BindingId, "Customer cancelled the service", None)).Succeeded);
        Assert.True((await iot.Bindings().BindAsync(h.ManagerId, otherProject, "ESP32-E2E", null, false, None)).Succeeded);
        Assert.Empty((await telemetry.GetHistoryAsync(otherCustomer, false, otherProject, null, null, null, null, None)).Value!.Items);
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.True((await telemetry.IngestAsync("ESP32-E2E", credential.Secret, 1.25, None)).Succeeded);
        Assert.Equal([1.25], (await telemetry.GetHistoryAsync(otherCustomer, false, otherProject, null, null, null, null, None)).Value!.Items.Select(r => r.CurrentAmps));
        Assert.Equal(3, (await telemetry.GetHistoryAsync(h.CustomerId, false, h.ProjectId, null, null, null, null, None)).Value!.Items.Count);
        Assert.Equal(IotErrorCodes.BindingNotFound, (await telemetry.GetLatestAsync(h.CustomerId, false, h.ProjectId, None)).ErrorCode);
        var first = (await iot.Monitoring().GetAsync(h.CustomerId, false, h.ProjectId, None)).Value!;
        Assert.Equal((false, (IotMeasuredMonitoringView?)null, baseline.Simulated), (first.MeasuredDataAvailable, first.Measured, first.Simulated));

        // Admin disables the device: readings refused, the second customer sees DISABLED and no current value.
        Assert.True((await iot.Devices().DisableAsync(h.AdminId, credential.Device.Id, "Returned for inspection", null, None)).Succeeded);
        Assert.Equal(IotErrorCodes.DeviceDisabled, (await telemetry.IngestAsync("ESP32-E2E", credential.Secret, 1.0, None)).ErrorCode);
        var disabled = (await telemetry.GetLatestAsync(otherCustomer, false, otherProject, None)).Value!;
        Assert.Equal((IotConnectionStatuses.Disabled, (IotReadingView?)null), (disabled.ConnectionStatus, disabled.Reading));
    }
}
