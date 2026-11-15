using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Modules.IotMonitoring;

/// <summary>
/// Manager/Admin attach a device to an installation project. Rules: one ACTIVE binding per device and one per project (both enforced by
/// partial unique indexes, so concurrent requests cannot create duplicates); a customer with several projects may have one device on each.
/// The project must exist and not be cancelled, the device must not be disabled. Releasing never deletes: the binding row stays as
/// history and every reading keeps pointing at the binding (so the project) it arrived under.
/// </summary>
public sealed class IotBindingHandlers(IIotMonitoringUnitOfWork unitOfWork, TimeProvider time)
{
    public Task<OperationResult<IotBindingView>> BindAsync(Guid actorUserId, Guid projectId, string? deviceCode, string? note, bool replaceCurrent, CancellationToken cancellationToken)
        => unitOfWork.ExecuteInTransactionAsync<OperationResult<IotBindingView>>(async token =>
        {
            var code = IotDeviceCredentials.NormalizeDeviceId(deviceCode);
            if (code is null) return (Validation<IotBindingView>("deviceId", "'deviceId' is not a valid device id."), false);
            var text = note?.Trim();
            if (text is { Length: > IotLimits.ReasonMaxLength }) return (Validation<IotBindingView>("note", $"'note' must be at most {IotLimits.ReasonMaxLength} characters."), false);

            var device = await unitOfWork.FindDeviceByCodeAsync(code, false, token);
            if (device is null) return (OperationResult<IotBindingView>.Fail(IotErrorCodes.DeviceNotFound, "IoT device was not found."), false);
            if (device.Status == IotDeviceStatuses.Disabled)
                return (OperationResult<IotBindingView>.Fail(IotErrorCodes.DeviceDisabled, "The device is disabled; enable it before binding."), false);

            var project = await unitOfWork.FindProjectAsync(projectId, token);
            if (project is null) return (OperationResult<IotBindingView>.Fail(InstallationErrorCodes.ProjectNotFound, "Installation project was not found."), false);
            if (project.Status == InstallationStatuses.Cancelled)
                return (OperationResult<IotBindingView>.Fail(IotErrorCodes.ProjectNotBindable, "A cancelled installation project cannot have a device."), false);

            var now = time.GetUtcNow();
            var deviceBinding = await unitOfWork.GetActiveBindingForDeviceAsync(device.Id, false, token);
            if (deviceBinding is not null)
            {
                if (deviceBinding.ProjectId == projectId) return (OperationResult<IotBindingView>.Ok(IotViewMapping.Binding(deviceBinding, device)), false);
                return (OperationResult<IotBindingView>.Fail(IotErrorCodes.DeviceAlreadyBound, "The device is bound to another installation project; release it first.",
                    new { bindingId = deviceBinding.Id, projectId = deviceBinding.ProjectId }), false);
            }

            // Only this project's current device can be replaced; the customer's other projects are never touched.
            var projectBinding = await unitOfWork.GetActiveBindingForProjectAsync(project.ProjectId, false, token);
            if (projectBinding is not null)
            {
                if (!replaceCurrent)
                    return (OperationResult<IotBindingView>.Fail(IotErrorCodes.ProjectAlreadyBound,
                        "The installation project already has a device; release it or bind with replaceCurrent = true.",
                        new { bindingId = projectBinding.Id, projectId = projectBinding.ProjectId }), false);
                var tracked = await unitOfWork.FindBindingAsync(projectBinding.Id, true, token);
                Release(tracked!, actorUserId, now, $"Replaced by device {device.DeviceId}");
            }

            var binding = new IotDeviceBinding
            {
                Id = Guid.NewGuid(), DeviceId = device.Id, ProjectId = project.ProjectId, CustomerUserId = project.CustomerUserId, Status = IotBindingStatuses.Active,
                BoundAt = now, BoundByUserId = actorUserId, Note = string.IsNullOrEmpty(text) ? null : text,
            };
            unitOfWork.Add(binding);
            try
            {
                await unitOfWork.SaveChangesAsync(token);
            }
            catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation)
            {
                // A concurrent bind won the race: the partial unique index names which rule it hit.
                var projectRule = ex.Constraint?.Contains("project", StringComparison.OrdinalIgnoreCase) == true;
                return (OperationResult<IotBindingView>.Fail(projectRule ? IotErrorCodes.ProjectAlreadyBound : IotErrorCodes.DeviceAlreadyBound,
                    projectRule ? "The installation project already has a device." : "The device is already bound to an installation project."), false);
            }
            return (OperationResult<IotBindingView>.Ok(IotViewMapping.Binding(binding, device)), true);
        }, cancellationToken);

    /// <summary>Detaches the device; the binding and its readings stay as history, the device becomes UNASSIGNED.</summary>
    public async Task<OperationResult<IotBindingView>> ReleaseAsync(Guid actorUserId, Guid bindingId, string? reason, CancellationToken cancellationToken)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > IotLimits.ReasonMaxLength)
            return Validation<IotBindingView>("reason", $"'reason' is required (at most {IotLimits.ReasonMaxLength} characters).");
        var binding = await unitOfWork.FindBindingAsync(bindingId, true, cancellationToken);
        if (binding is null) return OperationResult<IotBindingView>.Fail(IotErrorCodes.BindingNotFound, "Device binding was not found.");
        if (binding.Status != IotBindingStatuses.Active)
            return OperationResult<IotBindingView>.Fail(IotErrorCodes.BindingNotActive, "The binding was already released.");
        Release(binding, actorUserId, time.GetUtcNow(), text);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var device = await unitOfWork.FindDeviceAsync(binding.DeviceId, false, cancellationToken);
        return OperationResult<IotBindingView>.Ok(IotViewMapping.Binding(binding, device!));
    }

    public async Task<OperationResult<IReadOnlyList<IotBindingView>>> ListAsync(Guid? projectId, string? deviceCode, string? status, CancellationToken cancellationToken)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (normalizedStatus is not null && !IotBindingStatuses.All.Contains(normalizedStatus))
            return Validation<IReadOnlyList<IotBindingView>>("status", "'status' must be ACTIVE or RELEASED.");
        Guid? deviceId = null;
        if (!string.IsNullOrWhiteSpace(deviceCode))
        {
            var code = IotDeviceCredentials.NormalizeDeviceId(deviceCode);
            var device = code is null ? null : await unitOfWork.FindDeviceByCodeAsync(code, false, cancellationToken);
            if (device is null) return OperationResult<IReadOnlyList<IotBindingView>>.Ok([]);
            deviceId = device.Id;
        }
        var bindings = await unitOfWork.ListBindingsAsync(projectId, deviceId, normalizedStatus, cancellationToken);
        var devices = await unitOfWork.GetDevicesAsync(bindings.Select(b => b.DeviceId).Distinct().ToList(), cancellationToken);
        return OperationResult<IReadOnlyList<IotBindingView>>.Ok(bindings.Select(b => IotViewMapping.Binding(b, devices[b.DeviceId])).ToList());
    }

    private static void Release(IotDeviceBinding binding, Guid actorUserId, DateTimeOffset now, string reason)
        => (binding.Status, binding.ReleasedAt, binding.ReleasedByUserId, binding.ReleaseReason) = (IotBindingStatuses.Released, now, actorUserId, reason);

    private static OperationResult<T> Validation<T>(string field, string message)
        => OperationResult<T>.Fail(IotErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, string[]> { [field] = [message] });
}
