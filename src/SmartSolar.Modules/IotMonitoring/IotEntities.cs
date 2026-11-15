namespace SmartSolar.Modules.IotMonitoring;

/// <summary>
/// One registered current-measuring device (V1: an ESP32 with an ACS712 sensor). Authenticates with its own credential
/// (<see cref="SecretHash"/>; the raw secret is shown once, at registration or rotation), never with a user JWT.
/// </summary>
public sealed class IotDevice
{
    public Guid Id { get; set; }

    /// <summary>The identifier the firmware sends (normalised to upper case; unique).</summary>
    public string DeviceId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary><see cref="IotDeviceStatuses"/>: ACTIVE or DISABLED (an Admin lock; readings are refused while disabled).</summary>
    public string Status { get; set; } = null!;

    /// <summary>Lower-case hex SHA-256 of the raw secret; the secret itself is never stored.</summary>
    public string SecretHash { get; set; } = null!;

    /// <summary>Incremented on every rotation (reported to clients so a stale firmware credential can be recognised).</summary>
    public int SecretVersion { get; set; }

    public DateTimeOffset SecretRotatedAt { get; set; }

    /// <summary>Free text describing the sensor (for example "ACS712-30A").</summary>
    public string? SensorModel { get; set; }

    /// <summary>Largest |current| (A) the configured sensor can report; readings beyond it are rejected.</summary>
    public decimal MaxCurrentAmps { get; set; }

    /// <summary>Server time of the last accepted reading (any binding); null until the first one.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }
    public Guid? DisabledByUserId { get; set; }
    public string? DisabledReason { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// A device attached to one installation project. A customer may own several projects, each with its own device: the database allows
/// one ACTIVE binding per project and one per device. <see cref="CustomerUserId"/> is the project's owner frozen at binding time (history
/// and reporting); access is always decided on the project's current owner. Every reading keeps the binding (so the project) it arrived
/// under, even after the device moves to another project. Released bindings are kept as history.
/// </summary>
public sealed class IotDeviceBinding
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CustomerUserId { get; set; }

    /// <summary><see cref="IotBindingStatuses"/>: ACTIVE or RELEASED.</summary>
    public string Status { get; set; } = null!;

    public DateTimeOffset BoundAt { get; set; }
    public Guid BoundByUserId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedByUserId { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>
/// One measurement received from a device while it was bound. Only what the firmware really measures is stored: the ACS712 current
/// in ampere, as sent (sign included). Power, energy and grid direction need sensors the hardware does not have yet; they are added
/// as nullable columns when the firmware sends them, never derived here.
/// </summary>
public sealed class IotTelemetryReading
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid BindingId { get; set; }

    /// <summary>Measured current in ampere exactly as reported (finite, within the device's sensor range).</summary>
    public double CurrentAmps { get; set; }

    /// <summary>Server receipt time (UTC); the firmware sends no timestamp.</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}

public static class IotDeviceStatuses
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";
    public static readonly IReadOnlyList<string> All = [Active, Disabled];
}

public static class IotBindingStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
    public static readonly IReadOnlyList<string> All = [Active, Released];
}

/// <summary>Operational state of a device, computed from timestamps (no background worker).</summary>
public static class IotConnectionStatuses
{
    /// <summary>A reading arrived within <see cref="IotMonitoringOptions.OfflineAfterSeconds"/>.</summary>
    public const string Online = "ONLINE";

    /// <summary>Bound and has sent data, but nothing within the timeout.</summary>
    public const string Offline = "OFFLINE";

    /// <summary>Bound but no reading received under the current binding yet.</summary>
    public const string NoData = "NO_DATA";

    /// <summary>Locked by an Admin; readings are refused.</summary>
    public const string Disabled = "DISABLED";

    /// <summary>Active device without a binding; readings are refused until a Manager binds it.</summary>
    public const string Unassigned = "UNASSIGNED";
}

/// <summary>Availability of a measured quantity in the monitoring views.</summary>
public static class IotMeasurementStatuses
{
    public const string Measured = "MEASURED";

    /// <summary>The hardware has no sensor for this quantity: the value is null and nothing is derived.</summary>
    public const string NotAvailable = "NOT_AVAILABLE";
}

public static class IotErrorCodes
{
    public const string DeviceNotFound = "IOT_DEVICE_NOT_FOUND";
    public const string BindingNotFound = "IOT_BINDING_NOT_FOUND";
    public const string ValidationFailed = "IOT_VALIDATION_FAILED";
    public const string ReadingValidationFailed = "IOT_READING_VALIDATION_FAILED";
    public const string DeviceIdTaken = "IOT_DEVICE_ID_TAKEN";
    public const string DeviceDisabled = "IOT_DEVICE_DISABLED";
    public const string DeviceNotDisabled = "IOT_DEVICE_NOT_DISABLED";

    /// <summary>Unknown device id or wrong credential (401; the two cases are not distinguished).</summary>
    public const string DeviceUnauthorized = "IOT_DEVICE_UNAUTHORIZED";

    /// <summary>The device is registered and active but no Manager has bound it to a project (409).</summary>
    public const string DeviceNotBound = "IOT_DEVICE_NOT_BOUND";

    public const string DeviceAlreadyBound = "IOT_DEVICE_ALREADY_BOUND";
    /// <summary>The installation project already has an ACTIVE device (one device per project; 409).</summary>
    public const string ProjectAlreadyBound = "IOT_PROJECT_ALREADY_BOUND";
    public const string ProjectNotBindable = "IOT_PROJECT_NOT_BINDABLE";
    public const string BindingNotActive = "IOT_BINDING_NOT_ACTIVE";
    public const string StaleRevision = "IOT_STALE_REVISION";

    /// <summary>The database refused or timed out while storing a reading (503; the device retries on its next cycle).</summary>
    public const string StorageUnavailable = "IOT_STORAGE_UNAVAILABLE";
}

public static class IotLimits
{
    public const int DeviceIdMinLength = 3;
    public const int DeviceIdMaxLength = 64;
    public const int DisplayNameMaxLength = 150;
    public const int SensorModelMaxLength = 50;
    public const int ReasonMaxLength = 500;
    public const decimal MaxConfigurableCurrentAmps = 1000m;
}

/// <summary>Configuration section "IotMonitoring"; validated at startup.</summary>
public sealed class IotMonitoringOptions
{
    public const string SectionName = "IotMonitoring";

    /// <summary>A bound device is ONLINE while its last reading is at most this old (the firmware reports every 10 s).</summary>
    public int OfflineAfterSeconds { get; set; } = 30;

    /// <summary>Agreed firmware send period; documented to clients, not enforced here (the ingest rate limit allows jitter).</summary>
    public int ExpectedReportIntervalSeconds { get; set; } = 10;

    /// <summary>Sensor range used when a device is registered without its own (ACS712-30A).</summary>
    public decimal DefaultMaxCurrentAmps { get; set; } = 30m;

    public int DefaultHistoryLimit { get; set; } = 100;
    public int MaxHistoryLimit { get; set; } = 500;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (OfflineAfterSeconds is < 5 or > 86_400) problems.Add("OfflineAfterSeconds must be 5-86400.");
        if (ExpectedReportIntervalSeconds is < 1 or > 3600) problems.Add("ExpectedReportIntervalSeconds must be 1-3600.");
        if (DefaultMaxCurrentAmps <= 0 || DefaultMaxCurrentAmps > IotLimits.MaxConfigurableCurrentAmps) problems.Add($"DefaultMaxCurrentAmps must be in (0, {IotLimits.MaxConfigurableCurrentAmps}].");
        if (MaxHistoryLimit is < 1 or > 5000) problems.Add("MaxHistoryLimit must be 1-5000.");
        if (DefaultHistoryLimit < 1 || DefaultHistoryLimit > MaxHistoryLimit) problems.Add("DefaultHistoryLimit must be 1-MaxHistoryLimit.");
        return problems;
    }
}
