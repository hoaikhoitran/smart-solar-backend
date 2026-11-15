using System.Data.Common;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Modules.IotMonitoring;

/// <summary>
/// Telemetry ingestion from devices and the measured-data reads of customers and staff. A reading is accepted only from a registered,
/// enabled, bound device presenting its credential, and only when the value is a finite number within the device's sensor range; it is
/// stored as sent (no sign correction, no derived power or energy) with the server receipt time.
/// </summary>
public sealed class IotTelemetryHandlers(IIotMonitoringUnitOfWork unitOfWork, IotMonitoringOptions options, TimeProvider time, ILogger<IotTelemetryHandlers> logger)
{
    public async Task<OperationResult<IotIngestView>> IngestAsync(string? deviceCode, string? presentedSecret, double? current, CancellationToken cancellationToken)
    {
        var code = IotDeviceCredentials.NormalizeDeviceId(deviceCode);
        var device = code is null ? null : await unitOfWork.FindDeviceByCodeAsync(code, false, cancellationToken);
        // Unknown id and wrong secret get the same answer; the secret is never logged.
        if (device is null || !IotDeviceCredentials.Matches(presentedSecret, device.SecretHash))
        {
            logger.LogWarning("IoT reading refused: unknown device or wrong credential (device {DeviceId}).", code ?? "<invalid>");
            return OperationResult<IotIngestView>.Fail(IotErrorCodes.DeviceUnauthorized, "Unknown device or invalid device credential.");
        }
        if (device.Status == IotDeviceStatuses.Disabled)
            return OperationResult<IotIngestView>.Fail(IotErrorCodes.DeviceDisabled, "The device is disabled.");
        var binding = await unitOfWork.GetActiveBindingForDeviceAsync(device.Id, false, cancellationToken);
        if (binding is null)
            return OperationResult<IotIngestView>.Fail(IotErrorCodes.DeviceNotBound, "The device is not bound to an installation project; readings are not accepted.");
        if (ValidateCurrent(current, device) is { } invalid) return invalid;

        var now = time.GetUtcNow();
        var reading = new IotTelemetryReading { Id = Guid.NewGuid(), DeviceId = device.Id, BindingId = binding.Id, CurrentAmps = current!.Value, ReceivedAt = now };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.Add(reading);
                await unitOfWork.SaveChangesAsync(token);
                await unitOfWork.TouchDeviceAsync(device.Id, now, token);
                return (true, true);
            }, cancellationToken);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            logger.LogError(ex, "IoT reading of device {DeviceId} could not be stored.", device.DeviceId);
            return OperationResult<IotIngestView>.Fail(IotErrorCodes.StorageUnavailable, "The reading could not be stored; send the next one as scheduled.");
        }
        return OperationResult<IotIngestView>.Ok(new IotIngestView(reading.Id, now, reading.CurrentAmps, IotConnectionStatuses.Online));
    }

    private static OperationResult<IotIngestView>? ValidateCurrent(double? current, IotDevice device)
    {
        string? problem = null;
        if (current is null) problem = "'current' is required (ampere, a JSON number).";
        else if (double.IsNaN(current.Value) || double.IsInfinity(current.Value)) problem = "'current' must be a finite number.";
        else if (Math.Abs(current.Value) > (double)device.MaxCurrentAmps) problem = $"'current' must be within ±{device.MaxCurrentAmps:0.###} A (the device's sensor range).";
        return problem is null
            ? null
            : OperationResult<IotIngestView>.Fail(IotErrorCodes.ReadingValidationFailed, "The reading is invalid.", new Dictionary<string, string[]> { ["current"] = [problem] });
    }

    private static bool IsDatabaseFailure(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException!)
            if (ex is DbException or TimeoutException) return true;
        return false;
    }

    // ---- Reads (customer: own projects only; staff: any project) ----

    public async Task<OperationResult<IotProjectDeviceView>> GetProjectDeviceAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await ProjectAsync(userId, isStaff, projectId, cancellationToken);
        if (project is null) return ProjectNotFound<IotProjectDeviceView>();
        var current = await CurrentAsync(projectId, cancellationToken);
        if (current is null) return NoBinding<IotProjectDeviceView>();
        var (binding, device) = current.Value;
        var latest = await unitOfWork.GetLatestReadingAsync([binding.Id], cancellationToken);
        return OperationResult<IotProjectDeviceView>.Ok(IotViewMapping.ProjectDevice(device, binding, latest?.ReceivedAt, time.GetUtcNow(), options));
    }

    /// <summary>The newest reading of the current device; null while there is none, and always null while the device is DISABLED.</summary>
    public async Task<OperationResult<IotLatestReadingView>> GetLatestAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await ProjectAsync(userId, isStaff, projectId, cancellationToken);
        if (project is null) return ProjectNotFound<IotLatestReadingView>();
        var current = await CurrentAsync(projectId, cancellationToken);
        if (current is null) return NoBinding<IotLatestReadingView>();
        var (binding, device) = current.Value;
        var latest = await unitOfWork.GetLatestReadingAsync([binding.Id], cancellationToken);
        var status = IotViewMapping.ConnectionStatus(device, binding, latest?.ReceivedAt, time.GetUtcNow(), options);
        var reading = latest is null || device.Status == IotDeviceStatuses.Disabled ? null : IotViewMapping.Reading(latest, device.DeviceId);
        return OperationResult<IotLatestReadingView>.Ok(new IotLatestReadingView(status, reading));
    }

    /// <summary>
    /// Readings of the project, newest first, across every device that was ever bound to it (and only those bindings, so a device's
    /// earlier life with another customer never shows). <paramref name="before"/> is the exclusive cursor from the previous page.
    /// </summary>
    public async Task<OperationResult<IotReadingHistoryView>> GetHistoryAsync(Guid userId, bool isStaff, Guid projectId, DateTimeOffset? from, DateTimeOffset? to,
        DateTimeOffset? before, int? limit, CancellationToken cancellationToken)
    {
        var project = await ProjectAsync(userId, isStaff, projectId, cancellationToken);
        if (project is null) return ProjectNotFound<IotReadingHistoryView>();
        var errors = new Dictionary<string, string[]>();
        var size = limit ?? options.DefaultHistoryLimit;
        if (size < 1 || size > options.MaxHistoryLimit) errors["limit"] = [$"'limit' must be 1-{options.MaxHistoryLimit}."];
        if (from is { } f && to is { } t && f > t) errors["from"] = ["'from' must not be after 'to'."];
        if (errors.Count > 0) return OperationResult<IotReadingHistoryView>.Fail(IotErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

        var bindings = await unitOfWork.ListProjectBindingsAsync(projectId, cancellationToken);
        if (bindings.Count == 0) return OperationResult<IotReadingHistoryView>.Ok(new IotReadingHistoryView([], size, false, null, from, to));
        var devices = await unitOfWork.GetDevicesAsync(bindings.Select(b => b.DeviceId).Distinct().ToList(), cancellationToken);
        var codes = bindings.ToDictionary(b => b.Id, b => devices[b.DeviceId].DeviceId);
        var page = await unitOfWork.ListReadingsAsync(bindings.Select(b => b.Id).ToList(), from, to, before, size, cancellationToken);
        return OperationResult<IotReadingHistoryView>.Ok(new IotReadingHistoryView(page.Items.Select(r => IotViewMapping.Reading(r, codes[r.BindingId])).ToList(), size,
            page.HasMore, page.NextBefore, from, to));
    }

    /// <summary>The measured block of the energy monitoring view, or null when the project has no device bound (ownership is checked by the caller).</summary>
    public async Task<IotMeasuredMonitoringView?> MeasuredAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(projectId, cancellationToken);
        if (current is null) return null;
        var (binding, device) = current.Value;
        var latest = await unitOfWork.GetLatestReadingAsync([binding.Id], cancellationToken);
        var now = time.GetUtcNow();
        var reading = latest is null || device.Status == IotDeviceStatuses.Disabled ? null : IotViewMapping.Reading(latest, device.DeviceId);
        return new IotMeasuredMonitoringView(IotViewMapping.MeasuredSource, IotViewMapping.ProjectDevice(device, binding, latest?.ReceivedAt, now, options), reading,
            reading is null ? IotViewMapping.NotAvailable("A") : IotViewMapping.Measured(reading.CurrentAmps, "A"),
            IotViewMapping.NotAvailable("W"), IotViewMapping.NotAvailable("kWh"), IotViewMapping.NotAvailable("kWh"), IotViewMapping.NotAvailable("kWh"),
            IotViewMapping.MeasuredNotice);
    }

    private async Task<IotProjectInfo?> ProjectAsync(Guid userId, bool isStaff, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, cancellationToken);
        return project is null || (!isStaff && project.CustomerUserId != userId) ? null : project;
    }

    private async Task<(IotDeviceBinding Binding, IotDevice Device)?> CurrentAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var binding = await unitOfWork.GetActiveBindingForProjectAsync(projectId, false, cancellationToken);
        if (binding is null) return null;
        var device = await unitOfWork.FindDeviceAsync(binding.DeviceId, false, cancellationToken);
        return device is null ? null : (binding, device);
    }

    private static OperationResult<T> ProjectNotFound<T>() => OperationResult<T>.Fail(InstallationErrorCodes.ProjectNotFound, "Installation project was not found.");
    private static OperationResult<T> NoBinding<T>() => OperationResult<T>.Fail(IotErrorCodes.BindingNotFound, "No IoT device is bound to this installation project.");
}
