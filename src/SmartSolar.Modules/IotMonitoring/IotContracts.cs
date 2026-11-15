using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.IotMonitoring;

/// <summary>The installation project facts a binding needs (read from the Installation Flow tables; nothing is copied).</summary>
public sealed record IotProjectInfo(Guid ProjectId, Guid CustomerUserId, string Status, DateTimeOffset? HandoverAt);

/// <summary>One page of readings, newest first; <see cref="NextBefore"/> is the cursor for the next (older) page.</summary>
public sealed record IotReadingPage(IReadOnlyList<IotTelemetryReading> Items, bool HasMore, DateTimeOffset? NextBefore);

public interface IIotMonitoringUnitOfWork : ITransactionalUnitOfWork
{
    Task<IotDevice?> FindDeviceAsync(Guid id, bool track, CancellationToken cancellationToken);

    /// <summary>By the firmware identifier (already normalised by the caller).</summary>
    Task<IotDevice?> FindDeviceByCodeAsync(string deviceId, bool track, CancellationToken cancellationToken);

    Task<(IReadOnlyList<IotDevice> Items, int Total)> ListDevicesAsync(string? status, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Sets <see cref="IotDevice.LastSeenAt"/> without touching the revision (concurrent readings of one device never conflict).</summary>
    Task TouchDeviceAsync(Guid deviceId, DateTimeOffset seenAt, CancellationToken cancellationToken);

    Task<IotProjectInfo?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IotDeviceBinding?> FindBindingAsync(Guid bindingId, bool track, CancellationToken cancellationToken);
    Task<IotDeviceBinding?> GetActiveBindingForDeviceAsync(Guid deviceId, bool track, CancellationToken cancellationToken);
    Task<IotDeviceBinding?> GetActiveBindingForProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken);

    /// <summary>Every binding (ACTIVE and RELEASED) matching the filters, newest first.</summary>
    Task<IReadOnlyList<IotDeviceBinding>> ListBindingsAsync(Guid? projectId, Guid? deviceId, string? status, CancellationToken cancellationToken);

    /// <summary>Bindings of the project in binding order (the project's whole device history).</summary>
    Task<IReadOnlyList<IotDeviceBinding>> ListProjectBindingsAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Devices by id (for views that list bindings).</summary>
    Task<IReadOnlyDictionary<Guid, IotDevice>> GetDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken);

    /// <summary>The newest reading received under any of the given bindings.</summary>
    Task<IotTelemetryReading?> GetLatestReadingAsync(IReadOnlyCollection<Guid> bindingIds, CancellationToken cancellationToken);

    Task<IotReadingPage> ListReadingsAsync(IReadOnlyCollection<Guid> bindingIds, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? before, int limit,
        CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
}
