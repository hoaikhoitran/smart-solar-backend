using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.IotMonitoring;

/// <summary>
/// Ingestion and reads: credential checks, value validation (stored as sent, never corrected), server-side receipt time, the
/// ONLINE/OFFLINE/NO_DATA/DISABLED states from timestamps, cursor paging, and the energy monitoring view with and without a device.
/// </summary>
public sealed class IotTelemetryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_bound_device_reading_is_stored_with_the_server_time_and_served_to_the_customer()
    {
        await using var ctx = IotTestContext.Create();
        var b = await ctx.BoundAsync(await ctx.AdminAsync(), await ctx.ManagerAsync());
        var telemetry = ctx.Telemetry();

        var ingested = await telemetry.IngestAsync(b.Code.ToLowerInvariant(), b.Secret, 0.2145, None);
        Assert.True(ingested.Succeeded, ingested.Message);
        Assert.Equal((ctx.Clock.GetUtcNow(), 0.2145, IotConnectionStatuses.Online), (ingested.Value!.ReceivedAt, ingested.Value.CurrentAmps, ingested.Value.ConnectionStatus));
        var row = await ctx.Db.IotTelemetryReadings.AsNoTracking().SingleAsync();
        Assert.Equal((b.Device, b.BindingId, 0.2145, ctx.Clock.GetUtcNow()), (row.DeviceId, row.BindingId, row.CurrentAmps, row.ReceivedAt));

        var latest = await telemetry.GetLatestAsync(b.Customer, false, b.Project, None);
        Assert.Equal((IotConnectionStatuses.Online, 0.2145, b.Code), (latest.Value!.ConnectionStatus, latest.Value.Reading!.CurrentAmps, latest.Value.Reading.DeviceId));

        var monitoring = (await ctx.Monitoring().GetAsync(b.Customer, false, b.Project, None)).Value!;
        Assert.Equal((EnergyMonitoringValues.Simulated, true, EnergyMonitoringValues.MeasuredDataAvailable, IotViewMapping.MeasuredSource),
            (monitoring.DataSource, monitoring.MeasuredDataAvailable, monitoring.Status, monitoring.MeasuredDataSource));
        var measured = monitoring.Measured!;
        Assert.Equal((IotMeasurementStatuses.Measured, 0.2145, "A"), (measured.CurrentAmps.Status, measured.CurrentAmps.Value, measured.CurrentAmps.Unit));
        foreach (var quantity in new[] { measured.PowerWatts, measured.EnergyProducedKwh, measured.EnergyImportedKwh, measured.EnergyExportedKwh })
            Assert.Equal((IotMeasurementStatuses.NotAvailable, (double?)null), (quantity.Status, quantity.Value));
        Assert.Equal(IotConnectionStatuses.Online, measured.Device.ConnectionStatus);
        Assert.Equal(EnergyMonitoringValues.WithDeviceNotice, monitoring.Notice);

        // A negative value is a measurement too: stored as sent, no sign correction.
        ctx.Clock.Now += TimeSpan.FromSeconds(10);
        Assert.True((await telemetry.IngestAsync(b.Code, b.Secret, -1.5, None)).Succeeded);
        Assert.Equal(-1.5, (await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!.Reading!.CurrentAmps);
    }

    [Fact]
    public async Task Readings_are_refused_without_a_valid_credential_or_binding_or_when_disabled()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var b = await ctx.BoundAsync(admin, manager);
        var unbound = await ctx.DeviceAsync(admin, "ESP32-FREE");
        var telemetry = ctx.Telemetry();

        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync(b.Code, b.Secret + "x", 0.1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync(b.Code, null, 0.1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync("ESP32-404", b.Secret, 0.1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync("bad id!", b.Secret, 0.1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceNotBound, (await telemetry.IngestAsync(unbound.Code, unbound.Secret, 0.1, None)).ErrorCode);

        // A rotated credential invalidates the old one at once.
        var rotated = (await ctx.Devices().RotateCredentialAsync(admin, b.Device, null, None)).Value!;
        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync(b.Code, b.Secret, 0.1, None)).ErrorCode);
        Assert.True((await telemetry.IngestAsync(b.Code, rotated.Secret, 0.1, None)).Succeeded);

        Assert.True((await ctx.Devices().DisableAsync(admin, b.Device, "Tampered", null, None)).Succeeded);
        Assert.Equal(IotErrorCodes.DeviceDisabled, (await telemetry.IngestAsync(b.Code, rotated.Secret, 0.1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceUnauthorized, (await telemetry.IngestAsync(b.Code, "wrong", 0.1, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.IotTelemetryReadings.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(30.001)]
    [InlineData(-31.0)]
    public async Task Invalid_current_values_are_rejected_and_never_corrected(double? current)
    {
        await using var ctx = IotTestContext.Create();
        var b = await ctx.BoundAsync(await ctx.AdminAsync(), await ctx.ManagerAsync());

        var refused = await ctx.Telemetry().IngestAsync(b.Code, b.Secret, current, None);

        Assert.Equal(IotErrorCodes.ReadingValidationFailed, refused.ErrorCode);
        Assert.Contains("current", ((Dictionary<string, string[]>)refused.Details!).Keys);
        Assert.Equal(0, await ctx.Db.IotTelemetryReadings.CountAsync());
        Assert.Null((await ctx.Db.IotDevices.AsNoTracking().SingleAsync()).LastSeenAt);
    }

    [Fact]
    public async Task The_sensor_range_is_per_device_and_both_bounds_are_accepted()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var customer = await ctx.UserAsync("Customer");
        var project = await ctx.ProjectAsync(customer);
        var small = await ctx.DeviceAsync(admin, "ESP32-5A", maxCurrentAmps: 5m);
        Assert.True((await ctx.Bindings().BindAsync(manager, project, small.Code, null, false, None)).Succeeded);

        Assert.True((await ctx.Telemetry().IngestAsync(small.Code, small.Secret, 5.0, None)).Succeeded);
        Assert.True((await ctx.Telemetry().IngestAsync(small.Code, small.Secret, -5.0, None)).Succeeded);
        Assert.Equal(IotErrorCodes.ReadingValidationFailed, (await ctx.Telemetry().IngestAsync(small.Code, small.Secret, 5.001, None)).ErrorCode);
    }

    [Fact]
    public async Task Connection_status_follows_the_timeout_and_the_current_value_is_hidden_while_disabled()
    {
        await using var ctx = IotTestContext.Create();
        var (admin, manager) = (await ctx.AdminAsync(), await ctx.ManagerAsync());
        var b = await ctx.BoundAsync(admin, manager);
        var telemetry = ctx.Telemetry();

        Assert.Equal(IotConnectionStatuses.NoData, (await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!.ConnectionStatus);
        Assert.Null((await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!.Reading);

        Assert.True((await telemetry.IngestAsync(b.Code, b.Secret, 0.3, None)).Succeeded);
        ctx.Clock.Now += TimeSpan.FromSeconds(ctx.Options.OfflineAfterSeconds);
        Assert.Equal(IotConnectionStatuses.Online, (await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!.ConnectionStatus);
        ctx.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(IotConnectionStatuses.Offline, (await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!.ConnectionStatus);
        Assert.Equal(IotConnectionStatuses.Offline, (await telemetry.GetProjectDeviceAsync(b.Customer, false, b.Project, None)).Value!.ConnectionStatus);
        Assert.Equal(IotConnectionStatuses.Offline, (await ctx.Devices().GetAsync(b.Device, None)).Value!.ConnectionStatus);

        Assert.True((await ctx.Devices().DisableAsync(admin, b.Device, "Suspected tampering", null, None)).Succeeded);
        var latest = (await telemetry.GetLatestAsync(b.Customer, false, b.Project, None)).Value!;
        Assert.Equal((IotConnectionStatuses.Disabled, (IotReadingView?)null), (latest.ConnectionStatus, latest.Reading));
        // No current value while disabled: the view falls back to the simulation state (this bare project has none).
        var monitoring = (await ctx.Monitoring().GetAsync(b.Customer, false, b.Project, None)).Value!;
        Assert.Equal((false, EnergyMonitoringValues.SimulationUnavailable, IotConnectionStatuses.Disabled, IotMeasurementStatuses.NotAvailable),
            (monitoring.MeasuredDataAvailable, monitoring.Status, monitoring.Measured!.Device.ConnectionStatus, monitoring.Measured.CurrentAmps.Status));
        Assert.Null(monitoring.Measured.Latest);
        Assert.Single((await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, null, null, None)).Value!.Items);
    }

    [Fact]
    public async Task History_pages_newest_first_with_a_cursor_and_filters()
    {
        await using var ctx = IotTestContext.Create();
        var b = await ctx.BoundAsync(await ctx.AdminAsync(), await ctx.ManagerAsync());
        var telemetry = ctx.Telemetry();
        var start = ctx.Clock.GetUtcNow();
        for (var i = 0; i < 5; i++)
        {
            ctx.Clock.Now = start.AddSeconds(10 * i);
            Assert.True((await telemetry.IngestAsync(b.Code, b.Secret, i, None)).Succeeded);
        }

        var first = (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, null, 2, None)).Value!;
        Assert.Equal([4.0, 3.0], first.Items.Select(r => r.CurrentAmps));
        Assert.True(first.HasMore);
        Assert.Equal(start.AddSeconds(30), first.NextBefore);
        var second = (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, first.NextBefore, 2, None)).Value!;
        Assert.Equal([2.0, 1.0], second.Items.Select(r => r.CurrentAmps));
        var third = (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, second.NextBefore, 2, None)).Value!;
        Assert.Equal([0.0], third.Items.Select(r => r.CurrentAmps));
        Assert.False(third.HasMore);
        Assert.Null(third.NextBefore);

        var window = (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, start.AddSeconds(10), start.AddSeconds(30), null, null, None)).Value!;
        Assert.Equal([3.0, 2.0, 1.0], window.Items.Select(r => r.CurrentAmps));
        Assert.Equal(ctx.Options.DefaultHistoryLimit, window.Limit);
        Assert.Equal(IotErrorCodes.ValidationFailed, (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, null, 0, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.ValidationFailed, (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, null, null, null, ctx.Options.MaxHistoryLimit + 1, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.ValidationFailed, (await telemetry.GetHistoryAsync(b.Customer, false, b.Project, start.AddSeconds(30), start, null, null, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await telemetry.GetHistoryAsync(Guid.NewGuid(), false, b.Project, null, null, null, null, None)).ErrorCode);
    }

    [Fact]
    public async Task Without_a_device_the_simulated_monitoring_and_the_empty_reads_are_unchanged()
    {
        await using var ctx = IotTestContext.Create();
        var customer = await ctx.UserAsync("Customer");
        var project = await ctx.ProjectAsync(customer);

        var monitoring = (await ctx.Monitoring().GetAsync(customer, false, project, None)).Value!;
        Assert.Equal((EnergyMonitoringValues.Simulated, false, EnergyMonitoringValues.SimulationUnavailable, (string?)null, (IotMeasuredMonitoringView?)null),
            (monitoring.DataSource, monitoring.MeasuredDataAvailable, monitoring.Status, monitoring.MeasuredDataSource, monitoring.Measured));
        Assert.Equal(EnergyMonitoringValues.SimulatedOnlyNotice, monitoring.Notice);
        Assert.Equal((EnergyMonitoringValues.NotConfigured, 0), (monitoring.AlertsStatus, monitoring.Alerts.Count));

        var telemetry = ctx.Telemetry();
        Assert.Equal(IotErrorCodes.BindingNotFound, (await telemetry.GetProjectDeviceAsync(customer, false, project, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.BindingNotFound, (await telemetry.GetLatestAsync(customer, false, project, None)).ErrorCode);
        var history = (await telemetry.GetHistoryAsync(customer, false, project, null, null, null, null, None)).Value!;
        Assert.Empty(history.Items);
        Assert.False(history.HasMore);
    }
}
