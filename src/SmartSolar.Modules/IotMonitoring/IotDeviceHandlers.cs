using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.IotMonitoring;

public sealed record DeviceRegistrationInput(string? DeviceId, string? DisplayName, string? SensorModel, decimal? MaxCurrentAmps);

/// <summary>
/// Admin device registry: register (the secret is returned once), list, inspect, lock/unlock and rotate the credential. Bindings are the
/// Manager's job (<see cref="IotBindingHandlers"/>); readings never pass through here.
/// </summary>
public sealed class IotDeviceHandlers(IIotMonitoringUnitOfWork unitOfWork, IotMonitoringOptions options, TimeProvider time)
{
    public const int MaxPageSize = 100;

    public async Task<OperationResult<IotDeviceCredentialView>> RegisterAsync(Guid adminUserId, DeviceRegistrationInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var code = IotDeviceCredentials.NormalizeDeviceId(input.DeviceId);
        if (code is null) errors["deviceId"] = [$"'deviceId' must be {IotLimits.DeviceIdMinLength}-{IotLimits.DeviceIdMaxLength} characters of letters, digits, '.', '_' or '-'."];
        var name = input.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > IotLimits.DisplayNameMaxLength) errors["displayName"] = [$"'displayName' is required (at most {IotLimits.DisplayNameMaxLength} characters)."];
        var sensor = string.IsNullOrWhiteSpace(input.SensorModel) ? null : input.SensorModel.Trim();
        if (sensor is { Length: > IotLimits.SensorModelMaxLength }) errors["sensorModel"] = [$"'sensorModel' must be at most {IotLimits.SensorModelMaxLength} characters."];
        var max = input.MaxCurrentAmps ?? options.DefaultMaxCurrentAmps;
        if (max <= 0 || max > IotLimits.MaxConfigurableCurrentAmps) errors["maxCurrentAmps"] = [$"'maxCurrentAmps' must be greater than 0 and at most {IotLimits.MaxConfigurableCurrentAmps}."];
        if (errors.Count > 0) return Validation<IotDeviceCredentialView>(errors);

        if (await unitOfWork.FindDeviceByCodeAsync(code!, false, cancellationToken) is not null)
            return OperationResult<IotDeviceCredentialView>.Fail(IotErrorCodes.DeviceIdTaken, "A device with this id is already registered.");

        var now = time.GetUtcNow();
        var secret = IotDeviceCredentials.NewSecret();
        var device = new IotDevice
        {
            Id = Guid.NewGuid(), DeviceId = code!, DisplayName = name!, Status = IotDeviceStatuses.Active, SecretHash = IotDeviceCredentials.Hash(secret), SecretVersion = 1,
            SecretRotatedAt = now, SensorModel = sensor, MaxCurrentAmps = decimal.Round(max, 3), CreatedByUserId = adminUserId, CreatedAt = now, UpdatedAt = now, Revision = 1,
        };
        try
        {
            unitOfWork.Add(device);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation)
        {
            return OperationResult<IotDeviceCredentialView>.Fail(IotErrorCodes.DeviceIdTaken, "A device with this id is already registered.");
        }
        return OperationResult<IotDeviceCredentialView>.Ok(new IotDeviceCredentialView(View(device, null, now), secret, device.SecretVersion));
    }

    public async Task<OperationResult<PagedResult<IotDeviceView>>> ListAsync(string? status, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (normalizedStatus is not null && !IotDeviceStatuses.All.Contains(normalizedStatus)) errors["status"] = ["'status' must be ACTIVE or DISABLED."];
        if (page < 1) errors["page"] = ["'page' must be at least 1."];
        if (pageSize < 1 || pageSize > MaxPageSize) errors["pageSize"] = [$"'pageSize' must be 1-{MaxPageSize}."];
        if (errors.Count > 0) return Validation<PagedResult<IotDeviceView>>(errors);

        var (items, total) = await unitOfWork.ListDevicesAsync(normalizedStatus, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), page, pageSize, cancellationToken);
        var now = time.GetUtcNow();
        var views = new List<IotDeviceView>(items.Count);
        foreach (var device in items)
            views.Add(View(device, await unitOfWork.GetActiveBindingForDeviceAsync(device.Id, false, cancellationToken), now));
        return OperationResult<PagedResult<IotDeviceView>>.Ok(PagedResult<IotDeviceView>.Create(views, page, pageSize, total));
    }

    public async Task<OperationResult<IotDeviceView>> GetAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await unitOfWork.FindDeviceAsync(deviceId, false, cancellationToken);
        if (device is null) return NotFound<IotDeviceView>();
        return OperationResult<IotDeviceView>.Ok(View(device, await unitOfWork.GetActiveBindingForDeviceAsync(device.Id, false, cancellationToken), time.GetUtcNow()));
    }

    /// <summary>Locks the device: readings are refused and the customer no longer gets a current value, until <see cref="EnableAsync"/>.</summary>
    public Task<OperationResult<IotDeviceView>> DisableAsync(Guid adminUserId, Guid deviceId, string? reason, int? expectedRevision, CancellationToken cancellationToken)
        => UpdateAsync(deviceId, expectedRevision, cancellationToken, (device, now) =>
        {
            var text = reason?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > IotLimits.ReasonMaxLength)
                return Validation<IotDeviceView>(new Dictionary<string, string[]> { ["reason"] = [$"'reason' is required (at most {IotLimits.ReasonMaxLength} characters)."] });
            if (device.Status == IotDeviceStatuses.Disabled) return OperationResult<IotDeviceView>.Fail(IotErrorCodes.DeviceDisabled, "The device is already disabled.");
            (device.Status, device.DisabledAt, device.DisabledByUserId, device.DisabledReason) = (IotDeviceStatuses.Disabled, now, adminUserId, text);
            return null;
        });

    public Task<OperationResult<IotDeviceView>> EnableAsync(Guid adminUserId, Guid deviceId, int? expectedRevision, CancellationToken cancellationToken)
        => UpdateAsync(deviceId, expectedRevision, cancellationToken, (device, _) =>
        {
            if (device.Status != IotDeviceStatuses.Disabled) return OperationResult<IotDeviceView>.Fail(IotErrorCodes.DeviceNotDisabled, "The device is not disabled.");
            (device.Status, device.DisabledAt, device.DisabledByUserId, device.DisabledReason) = (IotDeviceStatuses.Active, null, null, null);
            return null;
        });

    /// <summary>Replaces the credential at once: the previous secret stops working with this call, the new one is returned once.</summary>
    public async Task<OperationResult<IotDeviceCredentialView>> RotateCredentialAsync(Guid adminUserId, Guid deviceId, int? expectedRevision, CancellationToken cancellationToken)
    {
        var secret = IotDeviceCredentials.NewSecret();
        var updated = await UpdateAsync(deviceId, expectedRevision, cancellationToken, (device, now) =>
        {
            device.SecretHash = IotDeviceCredentials.Hash(secret);
            device.SecretVersion += 1;
            device.SecretRotatedAt = now;
            return null;
        });
        return updated.Succeeded ? OperationResult<IotDeviceCredentialView>.Ok(new IotDeviceCredentialView(updated.Value!, secret, updated.Value!.SecretVersion)) : updated.As<IotDeviceCredentialView>();
    }

    private async Task<OperationResult<IotDeviceView>> UpdateAsync(Guid deviceId, int? expectedRevision, CancellationToken cancellationToken,
        Func<IotDevice, DateTimeOffset, OperationResult<IotDeviceView>?> change)
    {
        var device = await unitOfWork.FindDeviceAsync(deviceId, true, cancellationToken);
        if (device is null) return NotFound<IotDeviceView>();
        if (expectedRevision is { } expected && expected != device.Revision)
            return OperationResult<IotDeviceView>.Fail(IotErrorCodes.StaleRevision, "The device was changed by someone else; reload and try again.");
        var now = time.GetUtcNow();
        if (change(device, now) is { } refused) return refused;
        device.UpdatedAt = now;
        device.Revision += 1;
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.StaleRevision)
        {
            return OperationResult<IotDeviceView>.Fail(IotErrorCodes.StaleRevision, "The device was changed by someone else; reload and try again.");
        }
        return OperationResult<IotDeviceView>.Ok(View(device, await unitOfWork.GetActiveBindingForDeviceAsync(device.Id, false, cancellationToken), now));
    }

    /// <summary>The admin view; the connection status counts only readings received under the current binding.</summary>
    public IotDeviceView View(IotDevice device, IotDeviceBinding? activeBinding, DateTimeOffset now)
    {
        var lastUnderBinding = activeBinding is not null && device.LastSeenAt is { } seen && seen >= activeBinding.BoundAt ? seen : (DateTimeOffset?)null;
        return new IotDeviceView(device.Id, device.DeviceId, device.DisplayName, device.Status, device.SensorModel, device.MaxCurrentAmps, device.SecretVersion,
            device.SecretRotatedAt, device.LastSeenAt, IotViewMapping.ConnectionStatus(device, activeBinding, lastUnderBinding, now, options),
            activeBinding is null ? null : IotViewMapping.Binding(activeBinding, device), device.DisabledAt, device.DisabledReason, device.CreatedAt, device.UpdatedAt, device.Revision);
    }

    private static OperationResult<T> NotFound<T>() => OperationResult<T>.Fail(IotErrorCodes.DeviceNotFound, "IoT device was not found.");

    private static OperationResult<T> Validation<T>(Dictionary<string, string[]> errors)
        => OperationResult<T>.Fail(IotErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);
}
