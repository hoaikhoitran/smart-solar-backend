namespace SmartSolar.Modules.IotMonitoring;

/// <summary>Admin view of a device. Never carries the secret or its hash.</summary>
public sealed record IotDeviceView(Guid Id, string DeviceId, string DisplayName, string Status, string? SensorModel, decimal MaxCurrentAmps, int SecretVersion,
    DateTimeOffset SecretRotatedAt, DateTimeOffset? LastSeenAt, string ConnectionStatus, IotBindingView? CurrentBinding, DateTimeOffset? DisabledAt,
    string? DisabledReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision);

/// <summary>Returned once, at registration or rotation: the only time the raw secret leaves the server.</summary>
public sealed record IotDeviceCredentialView(IotDeviceView Device, string Secret, int SecretVersion);

/// <summary><c>deviceId</c> is the firmware identifier (as everywhere in the IoT views); <c>deviceRecordId</c> the registry row id.</summary>
public sealed record IotBindingView(Guid BindingId, Guid DeviceRecordId, string DeviceId, string DeviceDisplayName, Guid ProjectId, Guid CustomerUserId, string Status,
    DateTimeOffset BoundAt, Guid BoundByUserId, string? Note, DateTimeOffset? ReleasedAt, Guid? ReleasedByUserId, string? ReleaseReason);

/// <summary>What a customer (or staff reading a project) sees about the bound device: no credential data, no other customers' history.</summary>
public sealed record IotProjectDeviceView(Guid BindingId, string DeviceId, string DisplayName, string? SensorModel, decimal MaxCurrentAmps, string DeviceStatus,
    string ConnectionStatus, DateTimeOffset BoundAt, DateTimeOffset? LastReadingAt, int OfflineAfterSeconds, int ExpectedReportIntervalSeconds);

public sealed record IotReadingView(Guid ReadingId, string DeviceId, DateTimeOffset ReceivedAt, double CurrentAmps);

/// <summary>Current reading of the project's device; <see cref="Reading"/> is null when there is none, or when the device is DISABLED / unbound.</summary>
public sealed record IotLatestReadingView(string ConnectionStatus, IotReadingView? Reading);

public sealed record IotReadingHistoryView(IReadOnlyList<IotReadingView> Items, int Limit, bool HasMore, DateTimeOffset? NextBefore, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>A quantity the monitoring could show: <see cref="Status"/> says whether the hardware measures it.</summary>
public sealed record IotMeasuredQuantityView(string Status, double? Value, string Unit);

/// <summary>
/// The measured part of the energy monitoring view. Only the current (A) is measured by the V1 hardware; power, energy and grid
/// import/export are reported as NOT_AVAILABLE with null values until the firmware sends them. Nothing here is simulated.
/// </summary>
public sealed record IotMeasuredMonitoringView(string Source, IotProjectDeviceView Device, IotReadingView? Latest, IotMeasuredQuantityView CurrentAmps,
    IotMeasuredQuantityView PowerWatts, IotMeasuredQuantityView EnergyProducedKwh, IotMeasuredQuantityView EnergyImportedKwh, IotMeasuredQuantityView EnergyExportedKwh,
    string Notice);

/// <summary>Acknowledgement to the device.</summary>
public sealed record IotIngestView(Guid ReadingId, DateTimeOffset ReceivedAt, double CurrentAmps, string ConnectionStatus);

public static class IotViewMapping
{
    public const string MeasuredSource = "IOT_DEVICE";

    public const string MeasuredNotice =
        "Current (A) measured by the bound ACS712 sensor and received over HTTP every 10 s. Power, energy and grid import/export are not measured by this hardware "
        + "(no voltage or bidirectional meter): they are NOT_AVAILABLE and never derived from the current.";

    public static IotBindingView Binding(IotDeviceBinding b, IotDevice device)
        => new(b.Id, device.Id, device.DeviceId, device.DisplayName, b.ProjectId, b.CustomerUserId, b.Status, b.BoundAt, b.BoundByUserId, b.Note, b.ReleasedAt,
            b.ReleasedByUserId, b.ReleaseReason);

    public static IotReadingView Reading(IotTelemetryReading r, string deviceCode) => new(r.Id, deviceCode, r.ReceivedAt, r.CurrentAmps);

    /// <summary>The operational state from timestamps: DISABLED > UNASSIGNED > NO_DATA > ONLINE/OFFLINE.</summary>
    public static string ConnectionStatus(IotDevice device, IotDeviceBinding? activeBinding, DateTimeOffset? lastReadingAt, DateTimeOffset now, IotMonitoringOptions options)
    {
        if (device.Status == IotDeviceStatuses.Disabled) return IotConnectionStatuses.Disabled;
        if (activeBinding is null) return IotConnectionStatuses.Unassigned;
        if (lastReadingAt is null) return IotConnectionStatuses.NoData;
        return now - lastReadingAt.Value <= TimeSpan.FromSeconds(options.OfflineAfterSeconds) ? IotConnectionStatuses.Online : IotConnectionStatuses.Offline;
    }

    public static IotProjectDeviceView ProjectDevice(IotDevice device, IotDeviceBinding binding, DateTimeOffset? lastReadingAt, DateTimeOffset now, IotMonitoringOptions options)
        => new(binding.Id, device.DeviceId, device.DisplayName, device.SensorModel, device.MaxCurrentAmps, device.Status,
            ConnectionStatus(device, binding, lastReadingAt, now, options), binding.BoundAt, lastReadingAt, options.OfflineAfterSeconds, options.ExpectedReportIntervalSeconds);

    public static IotMeasuredQuantityView Measured(double value, string unit) => new(IotMeasurementStatuses.Measured, value, unit);
    public static IotMeasuredQuantityView NotAvailable(string unit) => new(IotMeasurementStatuses.NotAvailable, null, unit);
}
