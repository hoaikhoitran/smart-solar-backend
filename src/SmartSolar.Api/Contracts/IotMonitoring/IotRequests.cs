namespace SmartSolar.Api.Contracts.IotMonitoring;

/// <summary>
/// Admin registration. <c>deviceId</c> is the identifier the firmware sends (3–64 of letters, digits, '.', '_', '-'; stored upper-case);
/// <c>maxCurrentAmps</c> is the sensor range (defaults to <c>IotMonitoring:DefaultMaxCurrentAmps</c>). The response carries the secret once.
/// </summary>
public sealed record RegisterIotDeviceRequest(string? DeviceId, string? DisplayName, string? SensorModel, decimal? MaxCurrentAmps);

/// <summary>Admin lock: <c>reason</c> required (≤500); <c>expectedRevision</c> optional.</summary>
public sealed record DisableIotDeviceRequest(string? Reason, int? ExpectedRevision);

/// <summary>Manager/Admin attaches the device to the project's customer; <c>replaceCurrent</c> releases the customer's current device first.</summary>
public sealed record BindIotDeviceRequest(string? DeviceId, string? Note, bool? ReplaceCurrent);

public sealed record ReleaseIotBindingRequest(string? Reason);

/// <summary>The firmware payload: <c>{ "current": 0.2145 }</c>, ampere, a finite JSON number.</summary>
public sealed record TelemetryReadingRequest(double? Current);
